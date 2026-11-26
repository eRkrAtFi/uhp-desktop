using System;

namespace QemuManager.Models;

/// <summary>
/// The whole runnable setup the user creates, opens, and runs - see
/// vision/VISION.md's own data-model table. Replaces the single flat
/// <c>VmConfig</c> class (see Services.EnvironmentMigration's own
/// LegacyVmConfig, which is what that old shape is called now that it
/// only exists to be migrated away from) with the Platform/Firmware/
/// Workload/Debug split the vision calls for - not called "Environment"
/// directly because that name collides with System.Environment
/// everywhere this would otherwise need qualifying.
///
/// Persisted as one JSON file per Environment under
/// %APPDATA%\QemuManager\vms\&lt;Id&gt;.json by
/// <see cref="Services.EnvironmentStore"/> - same folder/naming VmStore
/// already used; only the shape inside each file changed, which
/// EnvironmentStore's own migration handles transparently on load.
/// </summary>
public class EnvironmentConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Environment";

    /// <summary>Which IEngine runs this - matches IEngine.Id exactly
    /// ("Qemu" or "RiscvEmu" today). Was VmConfig.Kind; renamed because
    /// "which engine" is what this has always actually meant, and every
    /// IEngine member already calls it that (see EngineRegistry.GetFor).</summary>
    public string EngineId { get; set; } = "Qemu";

    public Platform Platform { get; set; } = new();
    public Firmware Firmware { get; set; } = new();
    public Workload Workload { get; set; } = new();
    public DebugConfiguration Debug { get; set; } = new();

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Bumped only when this shape changes in a way old JSON
    /// cannot just deserialize straight into (a renamed/restructured
    /// field, not an added one with a sensible default) - see
    /// Services.EnvironmentMigration.CurrentSchemaVersion. A pre-
    /// migration VmConfig-shaped file has no such property at all, which
    /// JSON deserializes as 0 here - EnvironmentStore's own load treats
    /// that as "this still needs migrating," not a corrupt file.</summary>
    public int SchemaVersion { get; set; } = Services.EnvironmentMigration.CurrentSchemaVersion;
}
