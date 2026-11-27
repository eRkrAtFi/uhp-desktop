using System;
using System.Collections.Generic;

namespace QemuManager.Models;

/// <summary>How a <see cref="ConfigField"/> should be edited - enough
/// variety for every field IEngine.GetConfigSchema describes today,
/// nothing speculative beyond that.</summary>
public enum FieldType
{
    Text,
    MultilineText,
    Int,
    Bool,
    /// <summary>One of a fixed list of strings - see Choices.</summary>
    Choice,
    /// <summary>A path to an existing file, with a Browse button.</summary>
    FilePath,
}

/// <summary>One field of an engine's own configuration schema (vision/
/// VISION.md's roadmap item 1: "a configuration schema" - fields, types,
/// defaults, validation) - what EnvironmentEditWindow renders generically
/// instead of hand-laying-out separate controls per engine the way
/// VmEditWindow/RiscvEmuEditWindow used to. Get/Set read and write the
/// field's own value directly on an EnvironmentConfig instance (e.g.
/// Platform.MemoryMB) - explicit delegates, not reflection over a string
/// path, so a typo is a compile error instead of a silently-dropped field.</summary>
public sealed class ConfigField
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required FieldType Type { get; init; }

    /// <summary>Which heading this field's own row is grouped under in
    /// the editor - "Platform", "Firmware", "Workload", or "Debug",
    /// matching EnvironmentConfig's own sub-objects.</summary>
    public required string Group { get; init; }

    public required Func<EnvironmentConfig, object?> Get { get; init; }
    public required Action<EnvironmentConfig, object?> Set { get; init; }

    /// <summary>Only for FieldType.Choice. Null for a Choice field whose
    /// options are runtime-dependent rather than fixed (Platform.Architecture:
    /// whatever qemu-system-*.exe binaries settings.QemuDir actually has) -
    /// the editor asks IEngine.ListArchitectures for that one directly.</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>Only for FieldType.FilePath - an OpenFileDialog filter
    /// string, e.g. "Disk images (*.qcow2;*.img)|*.qcow2;*.img|All files
    /// (*.*)|*.*". Falls back to a generic "All files" filter if null.</summary>
    public string? FileFilter { get; init; }

    /// <summary>Short help text shown under the field, same role
    /// VmEditWindow/RiscvEmuEditWindow's own per-field hint TextBlocks
    /// already played.</summary>
    public string? Hint { get; init; }

    public bool Required { get; init; }

    /// <summary>Extra validation beyond Required (e.g. "memory must be a
    /// positive number") - returns an error message, or null if the
    /// value (already converted to this field's own CLR type) is valid.</summary>
    public Func<object?, string?>? Validate { get; init; }

    /// <summary>This field's own Required-ness, suggested default, and
    /// hint can depend on another VISIBLE, interactively-changeable
    /// field's current value (e.g. Platform.MachineType is required, and
    /// has a suggested default, only for certain Platform.Architecture
    /// values) - see <see cref="FieldDependency"/>. Null for a field with
    /// no such dependency (most of them). Resolved live against whatever
    /// control is showing OnKey's value, reactively (OnFieldChanged) -
    /// unlike VisibleWhen/RequiredWhen below, which are evaluated once,
    /// statically, against the whole EnvironmentConfig.</summary>
    public FieldDependency? DependsOn { get; init; }

    /// <summary>Whether this field's own row is shown at all, decided
    /// once when the editor opens from the whole EnvironmentConfig being
    /// edited - null means always visible. For a field whose relevance
    /// depends on Workload.Kind (e.g. Workload.ElfPath only for
    /// WorkloadKind.BareMetalElf) rather than on another interactively-
    /// changeable field: Workload.Kind is set once, by whichever New
    /// Environment template was used, and never changed from within the
    /// editor itself, so this never needs DependsOn's own reactive
    /// machinery (no row/OnFieldChanged wiring - there is nothing in the
    /// UI that could ever change Kind to react to).</summary>
    public Func<EnvironmentConfig, bool>? VisibleWhen { get; init; }

    /// <summary>Same idea as VisibleWhen, for Required-ness that depends
    /// on the whole EnvironmentConfig rather than on another field's live
    /// UI value - checked in addition to (not instead of) Required and
    /// DependsOn.IsRequired.</summary>
    public Func<EnvironmentConfig, bool>? RequiredWhen { get; init; }
}

/// <summary>Ties one ConfigField's Required/default/hint to another
/// field's current value, so that relationship lives in the schema
/// itself (IEngine.GetConfigSchema) rather than as hand-written,
/// per-window logic the schema-driven EnvironmentEditWindow cannot see -
/// the whole point of a "configuration schema" per vision/VISION.md's
/// roadmap item 1. Each function is called with the OTHER field's
/// (OnKey's) current raw value, read live from whatever control is
/// showing it - not a snapshot taken when the window opened.</summary>
public sealed class FieldDependency
{
    /// <summary>The other field's own Key (e.g. "Platform.Architecture").</summary>
    public required string OnKey { get; init; }

    /// <summary>Given OnKey's current value, what this field's own
    /// suggested default is - null means "no suggestion for this value."
    /// The editor only ever uses this to OFFER a value into an empty
    /// field (e.g. right after the user changes Architecture); it never
    /// overwrites a value the user (or a loaded Environment) already
    /// set.</summary>
    public Func<object?, object?>? SuggestedDefault { get; init; }

    /// <summary>Given OnKey's current value, is this field required right
    /// now? Checked in addition to (not instead of) the field's own
    /// static Required flag.</summary>
    public Func<object?, bool>? IsRequired { get; init; }

    /// <summary>Given OnKey's current value, an extra hint to show
    /// instead of the field's own static Hint - null means "fall back to
    /// the static Hint for this value."</summary>
    public Func<object?, string?>? Hint { get; init; }
}
