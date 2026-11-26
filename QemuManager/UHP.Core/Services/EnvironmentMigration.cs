using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using QemuManager.Models;

namespace QemuManager.Services;

/// <summary>The pre-Environment/Platform/Workload shape every VM config
/// on disk used to be (was simply called "VmConfig") - kept only so
/// <see cref="EnvironmentMigration"/> has something to deserialize an old
/// file into. Nothing outside this file should ever reference this class;
/// every current caller wants <see cref="EnvironmentConfig"/> instead.
/// Field-for-field identical to the original VmConfig, since its only job
/// now is to accurately parse exactly what that class used to write.</summary>
public class LegacyVmConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "New VM";
    public string Kind { get; set; } = "Qemu";
    public string Architecture { get; set; } = "x86_64";
    public int MemoryMB { get; set; } = 2048;
    public int CpuCores { get; set; } = 2;
    public string DiskPath { get; set; } = "";
    public string CdromPath { get; set; } = "";
    public string Accelerator { get; set; } = "tcg";
    public string NetworkMode { get; set; } = "user";
    public string Display { get; set; } = "gtk";
    public string BootOrder { get; set; } = "c";
    public string MachineType { get; set; } = "";
    public string FirmwarePath { get; set; } = "";
    public string ExtraArgs { get; set; } = "";
    public string KernelPath { get; set; } = "";
    public string InitrdPath { get; set; } = "";
    public string ShutdownCommand { get; set; } = "poweroff";
    public string EnterSends { get; set; } = "CR";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Converts a pre-Environment/Platform/Workload VM config file (see
/// <see cref="LegacyVmConfig"/>) into an <see cref="EnvironmentConfig"/> -
/// vision/VISION.md's own data model, built step by step from the single
/// flat VmConfig class this replaces. See <see cref="EnvironmentStore"/>
/// for where this gets called (on load, transparently) and how the
/// original file is backed up first.
/// </summary>
public static class EnvironmentMigration
{
    /// <summary>Bumped only when EnvironmentConfig's own shape changes in
    /// a way old JSON cannot just deserialize straight into any more. A
    /// file with no SchemaVersion property at all (every pre-Environment
    /// VmConfig-shaped file) deserializes that as 0 - less than this -
    /// which is exactly the signal NeedsMigration checks for.
    ///
    /// 1: the original Environment/Platform/Workload/Debug split.
    /// 2 (v0.4.1): Workload.KernelPath/InitrdPath/KernelCmdline became
    /// real fields for QEMU too (see VmProcessManager's own
    /// GetConfigSchema) - see MigrateV1ToV2 for what a v1 file needs.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>True if the raw JSON for one config file predates this
    /// schema version and needs converting before use - checked directly
    /// against the parsed document rather than "do the new fields happen
    /// to look empty", since an EnvironmentConfig genuinely saved with
    /// default-looking values must not be re-migrated every load.</summary>
    public static bool NeedsMigration(JsonElement root) =>
        !root.TryGetProperty(nameof(EnvironmentConfig.SchemaVersion), out var v)
        || v.ValueKind != JsonValueKind.Number
        || v.GetInt32() < CurrentSchemaVersion;

    /// <summary>The actual field-by-field conversion - every
    /// LegacyVmConfig field lands in exactly one place in the v1 shape,
    /// nothing dropped, nothing invented - then immediately brought up to
    /// CurrentSchemaVersion via MigrateV1ToV2, same as a v1 file loaded
    /// directly would be.</summary>
    public static EnvironmentConfig Migrate(LegacyVmConfig legacy) => MigrateV1ToV2(new EnvironmentConfig
    {
        Id = legacy.Id,
        Name = legacy.Name,
        EngineId = legacy.Kind,
        Platform = new Platform
        {
            Architecture = legacy.Architecture,
            MachineType = legacy.MachineType,
            CpuCores = legacy.CpuCores,
            MemoryMB = legacy.MemoryMB,
            Accelerator = legacy.Accelerator,
            NetworkMode = legacy.NetworkMode,
        },
        Firmware = new Firmware
        {
            FirmwarePath = legacy.FirmwarePath,
        },
        Workload = new Workload
        {
            DiskPath = legacy.DiskPath,
            CdromPath = legacy.CdromPath,
            KernelPath = legacy.KernelPath,
            InitrdPath = legacy.InitrdPath,
            BootOrder = legacy.BootOrder,
            ExtraArgs = legacy.ExtraArgs,
            ShutdownCommand = legacy.ShutdownCommand,
        },
        Debug = new DebugConfiguration
        {
            Display = legacy.Display,
            EnterSends = legacy.EnterSends,
        },
        CreatedUtc = legacy.CreatedUtc,
        SchemaVersion = 1,
    });

    /// <summary>Parses raw JSON as the old flat shape and migrates it all
    /// the way to CurrentSchemaVersion - what EnvironmentStore.LoadAll
    /// calls for a file with no SchemaVersion property at all. Preserves
    /// VmStore's own historical "a riscv-emu VM saved before MemoryMB
    /// existed gets riscv-emu's own 512 MB default, not QEMU's 2048"
    /// fix-up, checked the same way it always was: against the raw JSON
    /// actually having the property, not against whatever value ended up
    /// in the deserialized object.</summary>
    public static EnvironmentConfig MigrateFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var legacy = JsonSerializer.Deserialize<LegacyVmConfig>(json)
            ?? throw new InvalidDataException("Could not parse legacy VM config JSON.");

        if (legacy.Kind == "RiscvEmu" && !doc.RootElement.TryGetProperty(nameof(LegacyVmConfig.MemoryMB), out _))
            legacy.MemoryMB = 512;

        return Migrate(legacy);
    }

    /// <summary>v0.4.1: Workload.KernelPath/InitrdPath/KernelCmdline
    /// became real, first-class fields for QEMU too (see
    /// VmProcessManager.GetConfigSchema) - previously the only way to
    /// boot a kernel directly under QEMU was to hand-type
    /// "-kernel ... -initrd ... -append ..." into Workload.ExtraArgs.
    /// Brings a v1 EnvironmentConfig (every one saved under v0.4.0) up to
    /// date two different ways depending on engine:
    ///
    /// - riscv-emu: ExtraArgs only ever meant "the kernel command line"
    ///   for this engine (see RiscvEmuBoot's own --append) - its value
    ///   moves to KernelCmdline outright, and ExtraArgs is cleared (it
    ///   has no other meaning for riscv-emu to preserve).
    /// - QEMU: ExtraArgs is parsed with the same quoting rules
    ///   BuildArguments itself already used (see ArgumentTokenizer) for a
    ///   "-kernel X", "-initrd Y", and/or "-append Z" pair anywhere in
    ///   it - each one found is extracted into its own new field and
    ///   removed from ExtraArgs, leaving any other flags exactly as they
    ///   were. A QEMU Environment with none of those three flags in its
    ///   own ExtraArgs is unaffected beyond the version bump.</summary>
    public static EnvironmentConfig MigrateV1ToV2(EnvironmentConfig env)
    {
        if (env.SchemaVersion >= 2) return env;

        if (env.EngineId == "RiscvEmu")
        {
            if (string.IsNullOrWhiteSpace(env.Workload.KernelCmdline))
                env.Workload.KernelCmdline = env.Workload.ExtraArgs;
            env.Workload.ExtraArgs = "";
        }
        else if (env.EngineId == "Qemu" && !string.IsNullOrWhiteSpace(env.Workload.ExtraArgs))
        {
            var tokens = ArgumentTokenizer.Split(env.Workload.ExtraArgs);
            var remaining = new List<string>();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] == "-kernel" && i + 1 < tokens.Count && string.IsNullOrWhiteSpace(env.Workload.KernelPath))
                { env.Workload.KernelPath = tokens[++i]; continue; }
                if (tokens[i] == "-initrd" && i + 1 < tokens.Count && string.IsNullOrWhiteSpace(env.Workload.InitrdPath))
                { env.Workload.InitrdPath = tokens[++i]; continue; }
                if (tokens[i] == "-append" && i + 1 < tokens.Count && string.IsNullOrWhiteSpace(env.Workload.KernelCmdline))
                { env.Workload.KernelCmdline = tokens[++i]; continue; }
                remaining.Add(tokens[i]);
            }
            env.Workload.ExtraArgs = ArgumentTokenizer.Join(remaining);
        }

        env.SchemaVersion = 2;
        return env;
    }
}
