/* virtio-net's user-mode NAT backend (Phase 9). See VIRTIO_NET_BASE's own
 * remarks in emu.h for why this device exists and net_guest_mac's own
 * remarks (also in emu.h) for why the guest's MAC is defined here, not
 * duplicated in devices.c. Implements just enough of a slirp-like NAT -
 * ARP, DHCP, ICMP (ping), DNS, and TCP (wget) - for the guest to reach the
 * real internet through this one host process, the same role real QEMU's
 * `-netdev user` plays, with no TAP/bridge/driver install needed on
 * Windows (TAP would need installing a kernel driver - a persistent
 * system change outside what this project asks of anyone running it).
 *
 * Addressing is a tiny, fixed /24 "LAN" between the one guest and this
 * NAT - deliberately the same numbers real QEMU's own slirp defaults to
 * (not required to match, but doing so means anyone who has watched a
 * real QEMU guest DHCP-configure itself sees familiar numbers here too).
 *
 * Not differentially verified against real QEMU's own slirp the way every
 * other device in this project is: there is no practical way to byte-for-
 * byte compare two independent NAT implementations' handling of real
 * internet traffic against a real, live remote server - timing, that
 * server's own behavior, and either side's internal NAT bookkeeping are
 * not something exposed for comparison the way a disk image's bytes are.
 * What IS checked: the guest-observable outcome (ping gets a reply, wget
 * gets a page) matches what the same guest commands do under real QEMU's
 * `-netdev user`, by hand, the same way this project checked virtio-net's
 * own MMIO address/IRQ empirically rather than from the spec alone.
 */
#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <icmpapi.h>
#endif
#include "emu.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define NET_GUEST_IP    0x0A00020Fu  /* 10.0.2.15 */
#define NET_GATEWAY_IP  0x0A000202u  /* 10.0.2.2 - also doubles as the DNS server, for one less address to track */
#define NET_BROADCAST_IP 0xFFFFFFFFu
#define NET_NETMASK     0xFFFFFF00u  /* 255.255.255.0 */
#define NET_DNS_UPSTREAM "8.8.8.8"   /* real resolver this NAT relays DNS queries to */
#define NET_LEASE_SECS  86400u

/* struct virtio_net_hdr_mrg_rxbuf (12 bytes: the 10-byte legacy
 * virtio_net_hdr plus a trailing le16 num_buffers), not the plain 10-byte
 * one the spec says is enough when VIRTIO_NET_F_MRG_RXBUF is not
 * negotiated (which this device never offers - see VIRTIO_NET_F_MAC_BIT
 * in devices.c, the only feature bit besides VERSION_1 this device
 * advertises). Empirically, not from the spec: this kernel's virtio_net
 * driver still frames every transmitted packet with the 12-byte header
 * regardless, confirmed by a frame coming through 2 bytes short of a
 * valid Ethernet header until this was changed from 10 to 12 - a real,
 * observed mismatch, not a hypothetical one, kept here as the actual
 * fix rather than re-litigated every time this file is touched. */
#define NET_HDR_LEN 12u

const uint8_t net_guest_mac[6] = {0x52, 0x54, 0x00, 0x12, 0x34, 0x56};
static const uint8_t gateway_mac[6] = {0x52, 0x54, 0x00, 0x12, 0x34, 0x02};

/* --- small big-endian helpers - every on-wire field below is big-endian,
 * independent of this host's own endianness. --------------------------- */
static uint16_t rd16be(const uint8_t *p) { return (uint16_t)(((uint32_t)p[0] << 8) | p[1]); }
static void wr16be(uint8_t *p, uint16_t v) { p[0] = (uint8_t)(v >> 8); p[1] = (uint8_t)v; }
static uint32_t rd32be(const uint8_t *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | p[3];
}
static void wr32be(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)(v >> 24); p[1] = (uint8_t)(v >> 16); p[2] = (uint8_t)(v >> 8); p[3] = (uint8_t)v;
}

/* Standard internet checksum (RFC 1071): 16-bit ones'-complement sum,
 * folded, then complemented. `initial` lets TCP/UDP fold a pseudo-header
 * sum in before summing the segment itself. */
static uint16_t inet_csum(const uint8_t *data, size_t len, uint32_t initial)
{
    uint32_t sum = initial;
    size_t i;
    for (i = 0; i + 1 < len; i += 2) sum += ((uint32_t)data[i] << 8) | data[i + 1];
    if (len & 1) sum += (uint32_t)data[len - 1] << 8;
    while (sum >> 16) sum = (sum & 0xFFFFu) + (sum >> 16);
    return (uint16_t)~sum;
}

static uint32_t pseudo_header_sum(uint32_t src_ip, uint32_t dst_ip, uint8_t proto, uint16_t len)
{
    uint8_t buf[12];
    wr32be(buf, src_ip);
    wr32be(buf + 4, dst_ip);
    buf[8] = 0; buf[9] = proto;
    wr16be(buf + 10, len);
    uint32_t sum = 0;
    int i;
    for (i = 0; i < 12; i += 2) sum += ((uint32_t)buf[i] << 8) | buf[i + 1];
    return sum;
}

/* --- TCP connection table + pending-DNS table: this NAT's only persistent
 * host-side state, opaque to everything outside this file (see m->net's
 * own remarks in emu.h). ------------------------------------------------- */
#define MAX_TCP_CONN 16
#define MAX_DNS_QUERY 8
#define TCP_RECV_CHUNK 1400   /* comfortably under one Ethernet frame's MTU - no IP fragmentation needed */
/* The upload direction's own buffer: bytes the guest has sent (and this
 * NAT has already acked, since there is no lossy medium between guest
 * and NAT to warrant withholding the ack - see this file's own top
 * comment) but has not yet finished handing to the real socket via
 * send(). Needed because send() on a non-blocking socket can return a
 * short write, or WSAEWOULDBLOCK, under real backpressure from a slow
 * remote reader - found exactly this way, empirically, not anticipated
 * up front: a fast local round trip never exercises this path (the real
 * socket always had room), but pointing the same transfer at a
 * deliberately slow reader reproduced a confirmed, real data loss (a
 * ~5.24MB upload arrived as ~5.19MB) with the earlier version of this
 * code, which just dropped whatever send() didn't accept rather than
 * buffering and retrying it. The advertised receive window below is
 * derived from how much of this buffer is actually free, so a well-
 * behaved guest TCP stack throttles itself to match - genuine
 * backpressure, not just a bigger fixed buffer.  */
#define TCP_UPLOAD_BUF_SIZE 65536u

typedef enum { TCPC_FREE = 0, TCPC_CONNECTING, TCPC_ESTABLISHED, TCPC_CLOSING } TcpState;

typedef struct {
    TcpState state;
    SOCKET sock;
    uint32_t dst_ip;
    uint16_t dst_port;
    uint16_t src_port;        /* the guest's own ephemeral port - the rest of the 4-tuple is implicit (always NET_GUEST_IP/this dst_ip) */
    uint32_t guest_next_seq;  /* next seq byte we expect from the guest == what we last acked */
    uint32_t our_seq;         /* next seq byte we'll use sending guest-ward data */
    uint32_t guest_acked_seq; /* how far the guest has acked our own data */
    uint32_t guest_window;    /* the guest's last advertised receive window */
    int fin_sent, fin_rcvd;
    int fin_pending;          /* guest sent FIN, but upload_buf wasn't empty yet - shutdown(SD_SEND) is deferred until it drains */
    uint8_t upload_buf[TCP_UPLOAD_BUF_SIZE];
    uint32_t upload_buf_len;  /* bytes buffered here, acked to the guest, not yet handed to the real socket */
} TcpConn;

typedef struct {
    int in_use;
    SOCKET sock;
    uint16_t guest_port;   /* where to address the reply back to */
} DnsQuery;

typedef struct {
    TcpConn tcp[MAX_TCP_CONN];
    DnsQuery dns[MAX_DNS_QUERY];
    HANDLE icmp_handle;
} NetBackend;

/* Generic virtqueue machinery (virtq_push_used) lives in devices.c, where
 * virtio-blk's own request processing already needed it - declared here
 * via emu.h rather than duplicated, the same "one definition" reasoning
 * as net_guest_mac above. */

/* Pulls one empty buffer off the receiveq and fills it with a
 * virtio_net_hdr (all zero - no checksum offload, no GSO, matching the
 * feature bits this device actually offers) followed by `frame`. Drops
 * the frame if the guest has not posted a receive buffer (matching a real
 * NIC under backpressure - there is nowhere else for the bytes to go) or
 * if the buffer posted is smaller than the frame plus its header (same
 * resolution as a real NIC: truncate rather than overrun memory the
 * descriptor does not actually own). */
static void net_inject_frame(Machine *m, const uint8_t *frame, uint32_t framelen)
{
    CPU *c = &m->cpu;
    VirtQueue *q = &c->net_q[0];
    if (getenv("RISCV_EMU_NETDBG"))
        fprintf(stderr, "[net] inject_frame len=%u rx_ready=%d rx_num=%u avail=%u last_avail=%u\n",
                framelen, q->ready, q->num,
                (unsigned)phys_read16(m, q->driver_addr + 2), q->last_avail_idx);
    if (!q->ready || q->num == 0) return;

    uint16_t avail_idx = (uint16_t)phys_read16(m, q->driver_addr + 2);
    if (q->last_avail_idx == avail_idx) return;
    uint16_t slot = q->last_avail_idx % (uint16_t)q->num;
    uint16_t head = (uint16_t)phys_read16(m, q->driver_addr + 4 + (uint64_t)slot * 2);
    q->last_avail_idx++;

    uint64_t d = q->desc_addr + (uint64_t)head * 16;
    uint64_t daddr = phys_read64(m, d);
    uint32_t dlen = phys_read32(m, d + 8);

    uint32_t total = NET_HDR_LEN + framelen;
    if (total > dlen) total = dlen;
    uint32_t i;
    for (i = 0; i < NET_HDR_LEN && i < total; i++) phys_write8(m, daddr + i, 0);
    for (i = NET_HDR_LEN; i < total; i++) phys_write8(m, daddr + i, frame[i - NET_HDR_LEN]);

    virtq_push_used(m, q, head, total);
    c->net_isr |= 1;
}

/* Fills in the Ethernet + IPv4 headers around an already-complete,
 * already-checksummed protocol payload and returns the frame's total
 * length - shared by every reply builder below so the IPv4 header fields
 * (and the one checksum they need) are computed in exactly one place. */
static uint32_t build_eth_ip(uint8_t *frame, uint32_t src_ip, uint32_t dst_ip, uint8_t proto,
                              const uint8_t *payload, uint32_t paylen)
{
    memcpy(frame, net_guest_mac, 6);
    memcpy(frame + 6, gateway_mac, 6);
    wr16be(frame + 12, 0x0800);

    uint8_t *ip = frame + 14;
    ip[0] = 0x45; ip[1] = 0;
    wr16be(ip + 2, (uint16_t)(20 + paylen));
    wr16be(ip + 4, 0);
    wr16be(ip + 6, 0x4000);  /* flags=DF, fragment offset 0 - this NAT never fragments */
    ip[8] = 64;
    ip[9] = proto;
    wr16be(ip + 10, 0);
    wr32be(ip + 12, src_ip);
    wr32be(ip + 16, dst_ip);
    wr16be(ip + 10, inet_csum(ip, 20, 0));

    memcpy(frame + 34, payload, paylen);
    return 34 + paylen;
}

/* --- ARP: the guest only ever needs to resolve one address (its own
 * gateway), so this is a responder, not a real cache. ------------------- */
static void net_handle_arp(Machine *m, const uint8_t *f, uint32_t len)
{
    if (len < 14 + 28) return;
    const uint8_t *arp = f + 14;
    uint16_t opcode = rd16be(arp + 6);
    uint32_t target_ip = rd32be(arp + 24);
    if (opcode != 1 || target_ip != NET_GATEWAY_IP) return;  /* only "who has our gateway" gets answered */

    uint8_t reply[42];
    memcpy(reply, net_guest_mac, 6);
    memcpy(reply + 6, gateway_mac, 6);
    wr16be(reply + 12, 0x0806);
    wr16be(reply + 14, 1);      /* hw type: ethernet */
    wr16be(reply + 16, 0x0800); /* proto type: IPv4 */
    reply[18] = 6; reply[19] = 4;
    wr16be(reply + 20, 2);      /* opcode: reply */
    memcpy(reply + 22, gateway_mac, 6);
    wr32be(reply + 28, NET_GATEWAY_IP);
    memcpy(reply + 32, arp + 8, 6);            /* target mac = the requester's own mac */
    wr32be(reply + 38, rd32be(arp + 14));      /* target ip  = the requester's own ip */
    net_inject_frame(m, reply, sizeof(reply));
}

/* --- ICMP: a real ping, via the IP Helper API (the same mechanism
 * ping.exe itself uses) rather than a raw socket, which needs
 * Administrator on Windows and so cannot be assumed here. --------------- */
/* Builds and injects one ICMP echo reply "from" reply_src_ip, with the
 * given id/seq/payload - shared by the two cases below (a real reply
 * relayed back from IcmpSendEcho, and the gateway answering for itself). */
static void icmp_reply(Machine *m, uint32_t reply_src_ip, uint16_t id, uint16_t seq,
                        const uint8_t *data, uint32_t datalen)
{
    if (datalen > 1400) datalen = 1400;
    uint8_t icmp_reply_buf[8 + 1400];
    icmp_reply_buf[0] = 0; icmp_reply_buf[1] = 0;  /* type 0 = echo reply, code 0 */
    wr16be(icmp_reply_buf + 2, 0);
    wr16be(icmp_reply_buf + 4, id);
    wr16be(icmp_reply_buf + 6, seq);
    memcpy(icmp_reply_buf + 8, data, datalen);
    wr16be(icmp_reply_buf + 2, inet_csum(icmp_reply_buf, 8 + datalen, 0));

    uint8_t frame[14 + 20 + 8 + 1400];
    uint32_t framelen = build_eth_ip(frame, reply_src_ip, NET_GUEST_IP, 1, icmp_reply_buf, 8 + datalen);
    net_inject_frame(m, frame, framelen);
}

static void net_handle_icmp(Machine *m, NetBackend *nb, uint32_t dst_ip, const uint8_t *icmp, uint32_t len)
{
    if (len < 8 || icmp[0] != 8) return;  /* only echo request (type 8) */
    uint16_t id = rd16be(icmp + 4), seq = rd16be(icmp + 6);
    const uint8_t *data = icmp + 8;
    uint32_t datalen = len - 8;

    if (dst_ip == NET_GATEWAY_IP) {
        /* The guest is pinging this NAT's own gateway address, not asking
         * it to relay a ping anywhere - real routers answer those
         * themselves (confirmed against real QEMU's own slirp gateway,
         * which does exactly this for `ping 10.0.2.2`), so this echoes
         * the request straight back rather than handing it to
         * IcmpSendEcho against an address nothing on the host is
         * actually listening on. */
        icmp_reply(m, NET_GATEWAY_IP, id, seq, data, datalen);
        return;
    }

    if (nb->icmp_handle == NULL || nb->icmp_handle == INVALID_HANDLE_VALUE) return;
    if (datalen > 1400) datalen = 1400;
    uint8_t replybuf[sizeof(ICMP_ECHO_REPLY) + 1400 + 64];
    DWORD rc = IcmpSendEcho(nb->icmp_handle, htonl(dst_ip), (LPVOID)data, (WORD)datalen, NULL,
                             replybuf, (DWORD)sizeof(replybuf), 2000);
    if (rc == 0) return;  /* timeout/unreachable - dropped, same as a real lost echo */
    PICMP_ECHO_REPLY er = (PICMP_ECHO_REPLY)replybuf;
    if (er->Status != 0) return;
    icmp_reply(m, dst_ip, id, seq, (const uint8_t *)er->Data, er->DataSize);
}

/* --- DHCP: a minimal server with exactly one lease to hand out, always
 * the same one, to the one guest this NAT ever talks to. Real busybox
 * udhcpc (see boot/initramfs.cpio.gz's rcS) expects the real protocol's
 * DISCOVER->OFFER, REQUEST->ACK shape, not just "always ACK". ----------- */
static void net_handle_dhcp(Machine *m, const uint8_t *udp_payload, uint32_t len)
{
    if (len < 240) return;
    if (udp_payload[0] != 1) return;  /* op: must be BOOTREQUEST */
    uint32_t xid = rd32be(udp_payload + 4);
    const uint8_t *chaddr = udp_payload + 28;
    if (rd32be(udp_payload + 236) != 0x63825363u) return;  /* magic cookie */

    int msgtype = -1;
    uint32_t off = 240;
    while (off < len) {
        uint8_t opt = udp_payload[off];
        if (opt == 0xFF) break;
        if (opt == 0x00) { off++; continue; }
        if (off + 1 >= len) break;
        uint8_t optlen = udp_payload[off + 1];
        if (opt == 53 && optlen >= 1 && off + 2 < len) msgtype = udp_payload[off + 2];
        off += 2 + optlen;
    }
    if (getenv("RISCV_EMU_NETDBG"))
        fprintf(stderr, "[net] dhcp msgtype=%d xid=%08x\n", msgtype, xid);
    if (msgtype != 1 && msgtype != 3) return;  /* only DISCOVER(1) and REQUEST(3) get answered */
    uint8_t reply_type = (msgtype == 1) ? 2 : 5;  /* OFFER : ACK */

    uint8_t pkt[300];
    memset(pkt, 0, sizeof(pkt));
    pkt[0] = 2;  /* op: BOOTREPLY */
    pkt[1] = 1; pkt[2] = 6; pkt[3] = 0;
    wr32be(pkt + 4, xid);
    wr32be(pkt + 16, NET_GUEST_IP);  /* yiaddr */
    memcpy(pkt + 28, chaddr, 16);
    wr32be(pkt + 236, 0x63825363u);

    uint32_t o = 240;
    pkt[o++] = 53; pkt[o++] = 1; pkt[o++] = reply_type;
    pkt[o++] = 1;  pkt[o++] = 4; wr32be(pkt + o, NET_NETMASK); o += 4;
    pkt[o++] = 3;  pkt[o++] = 4; wr32be(pkt + o, NET_GATEWAY_IP); o += 4;   /* router */
    pkt[o++] = 6;  pkt[o++] = 4; wr32be(pkt + o, NET_GATEWAY_IP); o += 4;   /* dns */
    pkt[o++] = 51; pkt[o++] = 4; wr32be(pkt + o, NET_LEASE_SECS); o += 4;   /* lease time */
    pkt[o++] = 54; pkt[o++] = 4; wr32be(pkt + o, NET_GATEWAY_IP); o += 4;   /* server id */
    pkt[o++] = 0xFF;

    uint8_t udp[8 + 300];
    wr16be(udp, 67); wr16be(udp + 2, 68);
    wr16be(udp + 4, (uint16_t)(8 + o));
    wr16be(udp + 6, 0);  /* UDP checksum optional over IPv4 - 0 means "not computed" */
    memcpy(udp + 8, pkt, o);

    uint8_t frame[14 + 20 + 8 + 300];
    uint32_t framelen = build_eth_ip(frame, NET_GATEWAY_IP, NET_BROADCAST_IP, 17, udp, 8 + o);
    net_inject_frame(m, frame, framelen);
}

/* --- DNS: relayed, not resolved here - the query's own bytes go straight
 * to a real upstream resolver over a real UDP socket, and the reply's
 * bytes come straight back, repackaged. net_poll (below) is what notices
 * the reply has arrived; this just opens the socket and fires the
 * query. ------------------------------------------------------------------ */
static void net_handle_dns(Machine *m, NetBackend *nb, uint16_t guest_port, const uint8_t *payload, uint32_t len)
{
    (void)m;
    int i;
    for (i = 0; i < MAX_DNS_QUERY; i++) if (!nb->dns[i].in_use) break;
    if (i == MAX_DNS_QUERY) return;  /* no free slot - the guest's own resolver will just retry */

    SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s == INVALID_SOCKET) return;
    u_long nb_flag = 1;
    ioctlsocket(s, FIONBIO, &nb_flag);

    struct sockaddr_in dst;
    memset(&dst, 0, sizeof(dst));
    dst.sin_family = AF_INET;
    dst.sin_port = htons(53);
    dst.sin_addr.s_addr = inet_addr(NET_DNS_UPSTREAM);
    if (sendto(s, (const char *)payload, (int)len, 0, (struct sockaddr *)&dst, sizeof(dst)) == SOCKET_ERROR) {
        closesocket(s);
        return;
    }
    nb->dns[i].in_use = 1;
    nb->dns[i].sock = s;
    nb->dns[i].guest_port = guest_port;
}

/* --- TCP: a minimal NAT splicer, not a full stack. Reliability between
 * the guest and this NAT needs no retransmission logic at all - there is
 * no lossy medium between them, only in-process memory, so anything this
 * NAT hands the guest is already delivered; the real TCP stack on the far
 * side of the one socket this opens per connection supplies the only
 * actual unreliable hop's reliability. What this does still have to get
 * right: sequence/ack number translation, respecting the guest's
 * advertised window, and the handshake/FIN/RST state transitions. -------- */
static TcpConn *tcp_find(NetBackend *nb, uint32_t dst_ip, uint16_t dst_port, uint16_t src_port)
{
    int i;
    for (i = 0; i < MAX_TCP_CONN; i++) {
        TcpConn *t = &nb->tcp[i];
        if (t->state != TCPC_FREE && t->dst_ip == dst_ip && t->dst_port == dst_port && t->src_port == src_port)
            return t;
    }
    return NULL;
}

static void tcp_send_segment(Machine *m, TcpConn *t, uint8_t flags, const uint8_t *payload, uint32_t paylen)
{
    if (getenv("RISCV_EMU_NETDBG"))
        fprintf(stderr, "[net] tcp_send_segment flags=%02x our_seq=%u guest_next_seq=%u paylen=%u state=%d port=%u\n",
                flags, t->our_seq, t->guest_next_seq, paylen, t->state, t->src_port);
    uint8_t seg[20 + TCP_RECV_CHUNK];
    wr16be(seg, t->dst_port);
    wr16be(seg + 2, t->src_port);
    wr32be(seg + 4, t->our_seq);
    wr32be(seg + 8, t->guest_next_seq);
    seg[12] = 0x50;  /* data offset: 5 words, no options */
    seg[13] = flags;
    {
        /* Real backpressure, not a fixed value: how much more upload data
         * this NAT can still accept before upload_buf is full. Capped at
         * 65535 since this device never negotiates window scaling. */
        uint32_t free_space = TCP_UPLOAD_BUF_SIZE - t->upload_buf_len;
        wr16be(seg + 14, (uint16_t)(free_space > 65535u ? 65535u : free_space));
    }
    wr16be(seg + 16, 0);      /* checksum, filled below */
    wr16be(seg + 18, 0);      /* urgent pointer: unused */
    if (paylen) memcpy(seg + 20, payload, paylen);

    uint32_t seglen = 20 + paylen;
    uint32_t psum = pseudo_header_sum(t->dst_ip, NET_GUEST_IP, 6, (uint16_t)seglen);
    wr16be(seg + 16, inet_csum(seg, seglen, psum));

    uint8_t frame[14 + 20 + 20 + TCP_RECV_CHUNK];
    uint32_t framelen = build_eth_ip(frame, t->dst_ip, NET_GUEST_IP, 6, seg, seglen);
    net_inject_frame(m, frame, framelen);

    if (flags & 0x02) t->our_seq += 1;       /* SYN consumes one sequence number */
    if (flags & 0x01) t->our_seq += 1;       /* FIN consumes one sequence number */
    t->our_seq += paylen;
}

static void tcp_send_rst(Machine *m, uint32_t dst_ip, uint16_t dst_port, uint16_t src_port, uint32_t seq, uint32_t ack)
{
    uint8_t seg[20];
    wr16be(seg, dst_port);
    wr16be(seg + 2, src_port);
    wr32be(seg + 4, seq);
    wr32be(seg + 8, ack);
    seg[12] = 0x50;
    seg[13] = 0x14;  /* RST|ACK */
    wr16be(seg + 14, 0);
    wr16be(seg + 16, 0);
    wr16be(seg + 18, 0);
    uint32_t psum = pseudo_header_sum(dst_ip, NET_GUEST_IP, 6, 20);
    wr16be(seg + 16, inet_csum(seg, 20, psum));

    uint8_t frame[14 + 20 + 20];
    uint32_t framelen = build_eth_ip(frame, dst_ip, NET_GUEST_IP, 6, seg, 20);
    net_inject_frame(m, frame, framelen);
}

static void tcp_close(TcpConn *t)
{
    if (t->sock != INVALID_SOCKET) closesocket(t->sock);
    memset(t, 0, sizeof(*t));
    t->state = TCPC_FREE;
}

static void net_handle_tcp(Machine *m, NetBackend *nb, uint32_t dst_ip, uint16_t dst_port, uint16_t src_port,
                            const uint8_t *tcp, uint32_t len)
{
    if (len < 20) return;
    uint32_t seq = rd32be(tcp + 4);
    uint32_t ack = rd32be(tcp + 8);
    uint8_t data_off = (uint8_t)((tcp[12] >> 4) * 4);
    uint8_t flags = tcp[13];
    uint16_t window = rd16be(tcp + 14);
    if (data_off < 20 || data_off > len) return;
    const uint8_t *payload = tcp + data_off;
    uint32_t paylen = len - data_off;

    TcpConn *t = tcp_find(nb, dst_ip, dst_port, src_port);
    if (getenv("RISCV_EMU_NETDBG"))
        fprintf(stderr, "[net] tcp in: flags=%02x seq=%u ack=%u paylen=%u window=%u found=%d port=%u\n",
                flags, seq, ack, paylen, window, t != NULL, src_port);

    if (flags & 0x04) {  /* RST from the guest - drop our side too, no reply needed */
        if (t) tcp_close(t);
        return;
    }

    if (!t && (flags & 0x02) && !(flags & 0x10)) {  /* bare SYN: a new connection */
        int i;
        for (i = 0; i < MAX_TCP_CONN; i++) if (nb->tcp[i].state == TCPC_FREE) break;
        if (i == MAX_TCP_CONN) { tcp_send_rst(m, dst_ip, dst_port, src_port, 0, seq + 1); return; }
        t = &nb->tcp[i];
        memset(t, 0, sizeof(*t));
        t->dst_ip = dst_ip; t->dst_port = dst_port; t->src_port = src_port;
        t->guest_next_seq = seq + 1;
        t->our_seq = 1000;  /* an arbitrary ISN - nothing on the real wire ever sees this number */
        t->guest_window = window;

        SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (s == INVALID_SOCKET) { t->state = TCPC_FREE; tcp_send_rst(m, dst_ip, dst_port, src_port, 0, seq + 1); return; }
        u_long nbflag = 1;
        ioctlsocket(s, FIONBIO, &nbflag);
        struct sockaddr_in sa;
        memset(&sa, 0, sizeof(sa));
        sa.sin_family = AF_INET;
        sa.sin_port = htons(dst_port);
        /* A connection to this NAT's own gateway address (not a real
         * routable host from the Windows host's point of view - same
         * reasoning as net_handle_icmp's gateway special-case above) is
         * redirected to the host's own loopback interface instead of a
         * doomed real connect() attempt to a fictitious IP. Lets a plain
         * TCP listener on the host (`nc -l -p PORT`, or anything else)
         * receive a connection the guest makes to 10.0.2.2:PORT - a
         * deliberate convenience this NAT offers, not something real
         * QEMU's own slirp does by default either (it needs an explicit
         * hostfwd= rule for the same thing). */
        sa.sin_addr.s_addr = htonl(dst_ip == NET_GATEWAY_IP ? 0x7F000001u /* 127.0.0.1 */ : dst_ip);
        connect(s, (struct sockaddr *)&sa, sizeof(sa));  /* non-blocking: WSAEWOULDBLOCK is the expected result */
        t->sock = s;
        t->state = TCPC_CONNECTING;
        return;  /* SYN-ACK goes out once net_poll sees the real connect() finish */
    }

    if (!t) { tcp_send_rst(m, dst_ip, dst_port, src_port, 0, seq + 1); return; }

    t->guest_window = window;
    if (flags & 0x10) t->guest_acked_seq = ack;  /* ACK present */

    if (t->state == TCPC_CONNECTING) return;  /* guest ack for our SYN-ACK arrives via the ack field above only */

    if (paylen > 0 && seq == t->guest_next_seq) {
        /* Buffered here, not handed to send() directly - see
         * TCP_UPLOAD_BUF_SIZE's own comment on why: a direct send() can
         * legitimately accept fewer bytes than paylen (or none at all)
         * under real backpressure from a slow remote reader, and the
         * earlier version of this code just silently dropped whatever
         * send() didn't take, which really did lose bytes under test.
         * Accepting only as much as currently fits (and acking only
         * that much, not the whole segment) is what makes the window
         * advertised above a real throttle instead of a formality - a
         * correct TCP sender will not keep re-offering more than that
         * once it sees the window shrink. */
        uint32_t room = TCP_UPLOAD_BUF_SIZE - t->upload_buf_len;
        uint32_t accept = paylen < room ? paylen : room;
        if (getenv("RISCV_EMU_NETDBG"))
            fprintf(stderr, "[net] tcp upload seq=%u paylen=%u accept=%u buf_len=%u\n",
                    seq, paylen, accept, t->upload_buf_len);
        if (accept > 0) {
            memcpy(t->upload_buf + t->upload_buf_len, payload, accept);
            t->upload_buf_len += accept;
            t->guest_next_seq += accept;
            tcp_send_segment(m, t, 0x10, NULL, 0);  /* bare ACK, for exactly what was actually buffered */
        }
    }

    if (flags & 0x01) {  /* FIN from the guest */
        t->guest_next_seq = seq + paylen + 1;
        t->fin_rcvd = 1;
        /* shutdown(SD_SEND) is deferred (net_poll) until upload_buf is
         * fully drained - calling it here, with bytes still sitting in
         * upload_buf, would discard them: the OS has already been told
         * there is nothing more coming on this socket. */
        t->fin_pending = 1;
        tcp_send_segment(m, t, 0x10, NULL, 0);
        if (t->fin_sent && t->upload_buf_len == 0) tcp_close(t);
    }
}

/* --- entry points called from devices.c / main.c ------------------------ */

void net_init(Machine *m)
{
    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        fprintf(stderr, "riscv-emu: WSAStartup failed, --net ignored\n");
        return;
    }
    NetBackend *nb = calloc(1, sizeof(NetBackend));
    if (!nb) return;
    int i;
    for (i = 0; i < MAX_TCP_CONN; i++) nb->tcp[i].sock = INVALID_SOCKET;
    nb->icmp_handle = IcmpCreateFile();
    m->net = nb;
    fprintf(stderr, "riscv-emu: virtio-net attached (user-mode NAT: guest=10.0.2.15 gw/dns=10.0.2.2)\n");
}

static void net_handle_udp(Machine *m, NetBackend *nb, uint32_t dst_ip, const uint8_t *udp, uint32_t len)
{
    if (len < 8) return;
    uint16_t src_port = rd16be(udp);
    uint16_t dst_port = rd16be(udp + 2);
    uint16_t udp_len = rd16be(udp + 4);
    if (getenv("RISCV_EMU_NETDBG"))
        fprintf(stderr, "[net] udp src_port=%u dst_port=%u udp_len=%u ip_len=%u\n",
                src_port, dst_port, udp_len, len);
    if (udp_len < 8 || udp_len > len) return;
    const uint8_t *payload = udp + 8;
    uint32_t paylen = udp_len - 8;

    if (dst_port == 67) net_handle_dhcp(m, payload, paylen);
    else if (dst_port == 53) net_handle_dns(m, nb, src_port, payload, paylen);
    (void)dst_ip;
}

static void net_handle_ip(Machine *m, NetBackend *nb, const uint8_t *f, uint32_t len)
{
    if (len < 14 + 20) return;
    const uint8_t *ip = f + 14;
    uint8_t ihl = (uint8_t)((ip[0] & 0x0F) * 4);
    if (ihl < 20 || len < 14u + ihl) return;
    uint8_t proto = ip[9];
    uint32_t src_ip = rd32be(ip + 12);
    uint32_t dst_ip = rd32be(ip + 16);
    const uint8_t *payload = ip + ihl;
    uint32_t paylen = len - 14 - ihl;
    /* src_ip 0.0.0.0 is legitimate here, not a filtered-out stranger: a
     * DHCPDISCOVER is sent from 0.0.0.0 precisely because the guest does
     * not have NET_GUEST_IP (or any address) yet - that is the one
     * problem DHCP exists to solve, so rejecting it by source address
     * would reject the only packet this NAT's DHCP responder ever needs
     * to see. */
    if (src_ip != NET_GUEST_IP && src_ip != 0) return;

    if (proto == 1) net_handle_icmp(m, nb, dst_ip, payload, paylen);
    else if (proto == 17) net_handle_udp(m, nb, dst_ip, payload, paylen);
    else if (proto == 6) {
        if (paylen < 4) return;
        uint16_t src_port = rd16be(payload);
        uint16_t dst_port = rd16be(payload + 2);
        net_handle_tcp(m, nb, dst_ip, dst_port, src_port, payload, paylen);
    }
}

static void net_handle_frame(Machine *m, NetBackend *nb, const uint8_t *f, uint32_t len)
{
    if (len < 14) return;
    uint16_t ethertype = rd16be(f + 12);
    if (getenv("RISCV_EMU_NETDBG"))
        fprintf(stderr, "[net] tx frame len=%u ethertype=%04x\n", len, ethertype);
    if (ethertype == 0x0806) net_handle_arp(m, f, len);
    else if (ethertype == 0x0800) net_handle_ip(m, nb, f, len);
    /* anything else (IPv6, ...): silently dropped, same as a NIC with no matching protocol handler */
}

void net_transmit(Machine *m)
{
    CPU *c = &m->cpu;
    NetBackend *nb = (NetBackend *)m->net;
    if (!nb) return;
    VirtQueue *q = &c->net_q[1];
    if (!q->ready || q->num == 0) return;

    uint16_t avail_idx = (uint16_t)phys_read16(m, q->driver_addr + 2);
    while (q->last_avail_idx != avail_idx) {
        uint16_t slot = q->last_avail_idx % (uint16_t)q->num;
        uint16_t head = (uint16_t)phys_read16(m, q->driver_addr + 4 + (uint64_t)slot * 2);
        q->last_avail_idx++;

        static uint8_t buf[2048];
        uint32_t buflen = 0;
        uint16_t idx = head;
        int seen = 0;
        for (;;) {
            uint64_t d = q->desc_addr + (uint64_t)idx * 16;
            uint64_t daddr = phys_read64(m, d);
            uint32_t dlen = phys_read32(m, d + 8);
            uint16_t dflags = (uint16_t)phys_read16(m, d + 12);
            uint16_t dnext = (uint16_t)phys_read16(m, d + 14);
            if (getenv("RISCV_EMU_NETDBG"))
                fprintf(stderr, "[net] tx desc idx=%u daddr=%llx dlen=%u dflags=%04x dnext=%u buflen_before=%u\n",
                        idx, (unsigned long long)daddr, dlen, dflags, dnext, buflen);
            uint32_t i;
            for (i = 0; i < dlen && buflen < sizeof(buf); i++) buf[buflen++] = phys_read8(m, daddr + i);
            seen++;
            if (!(dflags & 1) || seen > 64) break;
            idx = dnext;
        }

        virtq_push_used(m, q, head, 0);  /* transmitq: the length field is conventionally unused */

        if (buflen > NET_HDR_LEN) net_handle_frame(m, nb, buf + NET_HDR_LEN, buflen - NET_HDR_LEN);
    }
    c->net_isr |= 1;
}

void net_poll(Machine *m)
{
    NetBackend *nb = (NetBackend *)m->net;
    if (!nb) return;

    int i;
    for (i = 0; i < MAX_TCP_CONN; i++) {
        TcpConn *t = &nb->tcp[i];
        if (t->state == TCPC_FREE) continue;

        fd_set wfds, efds;
        FD_ZERO(&wfds); FD_ZERO(&efds);
        FD_SET(t->sock, &wfds); FD_SET(t->sock, &efds);
        struct timeval tv = {0, 0};

        if (t->state == TCPC_CONNECTING) {
            if (select(0, NULL, &wfds, &efds, &tv) > 0) {
                if (FD_ISSET(t->sock, &efds)) {
                    tcp_send_rst(m, t->dst_ip, t->dst_port, t->src_port, t->our_seq, t->guest_next_seq);
                    tcp_close(t);
                    continue;
                }
                if (FD_ISSET(t->sock, &wfds)) {
                    t->state = TCPC_ESTABLISHED;
                    tcp_send_segment(m, t, 0x12, NULL, 0);  /* SYN|ACK */
                }
            }
            continue;
        }

        if (t->state != TCPC_ESTABLISHED) continue;

        /* Drain upload_buf toward the real socket - independent of
         * fin_sent below (that only concerns the guest-ward direction);
         * data the guest already handed us needs to keep draining even
         * after this NAT has finished sending its own data to the guest.
         * A short/absent send() here just leaves the remainder for next
         * time, exactly the behavior whose absence caused the real data
         * loss this buffer exists to prevent. */
        if (t->upload_buf_len > 0) {
            uint32_t before = t->upload_buf_len;
            int sent = send(t->sock, (const char *)t->upload_buf, (int)t->upload_buf_len, 0);
            if (sent > 0) {
                memmove(t->upload_buf, t->upload_buf + sent, t->upload_buf_len - (uint32_t)sent);
                t->upload_buf_len -= (uint32_t)sent;
            }
            /* A TCP receiver whose window has shrunk toward (or hit) zero
             * has to proactively announce once it opens back up - nothing
             * obliges the sender to keep probing on its own, and in
             * practice the guest's own TCP stack just stops sending and
             * waits. Without this, draining upload_buf here is invisible
             * to the guest: the window value only ever goes out piggy-
             * backed on an ACK/data segment we send for some OTHER
             * reason, and once the guest has gone quiet waiting for more
             * room, there is no other reason left - a real deadlock,
             * found by actually driving this path with a slow enough
             * real-socket drain rate to exhaust the buffer. A bare ACK
             * whenever draining actually freed space is the fix. */
            if (t->upload_buf_len < before) tcp_send_segment(m, t, 0x10, NULL, 0);
        }
        if (t->fin_pending && t->upload_buf_len == 0) {
            shutdown(t->sock, SD_SEND);
            t->fin_pending = 0;
            if (t->fin_sent) { tcp_close(t); continue; }
        }

        if (t->fin_sent) continue;  /* already told the guest we are done sending - recv() would
                                      * just keep reporting the same EOF forever, re-sending FIN
                                      * each time with a bumped (and now wrong) sequence number */

        /* Only read more from the real socket if the guest's advertised
         * window still has room for it - real flow control, not just a
         * fixed cap, since the remote side's own data keeps arriving
         * whether or not the guest is ready for it. */
        uint32_t in_flight = t->our_seq - t->guest_acked_seq;
        if (in_flight + TCP_RECV_CHUNK <= t->guest_window) {
            fd_set rfds2;
            struct timeval tv2 = {0, 0};
            FD_ZERO(&rfds2);
            FD_SET(t->sock, &rfds2);
            if (select(0, &rfds2, NULL, NULL, &tv2) > 0) {
                uint8_t chunk[TCP_RECV_CHUNK];
                int got = recv(t->sock, (char *)chunk, sizeof(chunk), 0);
                if (got > 0) {
                    tcp_send_segment(m, t, 0x18, chunk, (uint32_t)got);  /* PSH|ACK */
                } else if (got == 0) {
                    tcp_send_segment(m, t, 0x11, NULL, 0);  /* FIN|ACK */
                    t->fin_sent = 1;
                    if (t->fin_rcvd) tcp_close(t);
                }
            }
        }
    }

    for (i = 0; i < MAX_DNS_QUERY; i++) {
        DnsQuery *d = &nb->dns[i];
        if (!d->in_use) continue;
        fd_set rfds; FD_ZERO(&rfds); FD_SET(d->sock, &rfds);
        struct timeval tv = {0, 0};
        if (select(0, &rfds, NULL, NULL, &tv) <= 0) continue;

        uint8_t reply[1024];
        int got = recv(d->sock, (char *)reply, sizeof(reply), 0);
        if (got > 0) {
            uint8_t udp[8 + 1024];
            wr16be(udp, 53); wr16be(udp + 2, d->guest_port);
            wr16be(udp + 4, (uint16_t)(8 + got));
            wr16be(udp + 6, 0);
            memcpy(udp + 8, reply, (size_t)got);
            uint8_t frame[14 + 20 + 8 + 1024];
            uint32_t framelen = build_eth_ip(frame, NET_GATEWAY_IP, NET_GUEST_IP, 17, udp, 8 + (uint32_t)got);
            net_inject_frame(m, frame, framelen);
        }
        closesocket(d->sock);
        d->in_use = 0;
    }
}
