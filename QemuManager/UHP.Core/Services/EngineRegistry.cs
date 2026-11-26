using System;
using System.Collections.Generic;
using System.Linq;
using QemuManager.Models;

namespace QemuManager.Services;

/// <summary>
/// Every <see cref="IEngine"/> this app knows about - the one place
/// (VmService's own constructor, where this is built) a new engine gets
/// added later (see vision/VISION.md's roadmap item 5, "a third engine to
/// prove the engine interface"). VmService looks an engine up here by
/// EnvironmentConfig instead of branching on EnvironmentConfig.EngineId itself.
/// </summary>
public class EngineRegistry
{
    private readonly List<IEngine> _engines;

    public EngineRegistry(params IEngine[] engines) => _engines = engines.ToList();

    public IReadOnlyList<IEngine> All => _engines;

    /// <summary>The engine that owns env (today: an exact
    /// EnvironmentConfig.EngineId match - see IEngine.CanHandle). Throws
    /// for an EngineId no registered engine recognizes, same as an
    /// EnvironmentConfig this app could never have produced itself; every
    /// caller already only ever sees one EnvironmentStore loaded or
    /// CreateDraft built, both of which always carry a real engine's own
    /// Id.</summary>
    public IEngine GetFor(EnvironmentConfig env) =>
        _engines.FirstOrDefault(e => e.CanHandle(env))
        ?? throw new InvalidOperationException($"No registered engine handles EngineId '{env.EngineId}'.");

    /// <summary>Looked up by Id directly - what CreateDraft(kind) needs
    /// before any EnvironmentConfig instance exists yet to ask GetFor about.</summary>
    public IEngine Get(string id) =>
        _engines.FirstOrDefault(e => e.Id == id)
        ?? throw new InvalidOperationException($"No registered engine with id '{id}'.");
}
