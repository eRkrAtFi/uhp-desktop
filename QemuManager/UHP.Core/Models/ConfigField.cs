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
}
