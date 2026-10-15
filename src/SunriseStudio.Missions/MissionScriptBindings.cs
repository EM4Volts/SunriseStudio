namespace SunriseStudio.Missions;

/** Only top-level require bindings and plain member aliases may become generated SDK references. */
public sealed class MissionScriptBindings
{
    private readonly Dictionary<string, string> aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> expanded = new(StringComparer.Ordinal);
    private bool multipleMissions;
    public string? MissionVariable { get; private set; }
    public string? MissionKey { get; private set; }
    /** Source offset just after the mission require; a new top-level require goes here. */
    public int? MissionEnd { get; private set; }

    public MissionScriptBindings(string source) : this(LuaScript.Read(source)) { }

    /** Reads the top-level locals in order; a later assignment to an alias ends it. */
    internal MissionScriptBindings(LuaScript script)
    {
        foreach (var statement in script.Chunk.Statements)
        {
            if (statement is LuaLocalStatement local) ReadLocal(local);
            else if (statement is LuaAssignmentStatement assignment)
                // Assigning to an alias or one of its members ends the alias.
                foreach (var target in assignment.Targets)
                    if (Path(target) is { } path && aliases.ContainsKey(path.Split('.')[0]))
                        Invalidate(path.Split('.')[0]);
        }
    }

    /** Names bind in order and the first value that is not an SDK binding stops the statement. */
    private void ReadLocal(LuaLocalStatement local)
    {
        if (local.EqualsSign is null || local.Names.Any(name => name.Attribute is not null)) return;
        for (int i = 0; i < local.Names.Count; i++)
        {
            string name = local.Names[i].Name;
            if (aliases.ContainsKey(name)) Invalidate(name);
            if (i >= local.Values.Count) return;
            var value = local.Values[i];
            var argument = Require(value);
            string? module = (argument as LuaLiteral)?.StringValue, canonical = null, missionKey = null;
            if (value is LuaMember { Name: "EventKind", Target: var sdk } && (Require(sdk) as LuaLiteral)?.StringValue == "sunrise.activity_sdk")
                canonical = "activity_sdk.EventKind";
            else if (module is "missions") canonical = module;
            else if (module is "lib.campaign" or "lib.flow" or "lib.mission_lib") canonical = module[4..];
            else if (module is "sunrise.activity_sdk") canonical = "activity_sdk";
            else if (argument is not null && Path(argument) is { } key && Expand(key).StartsWith("missions.", StringComparison.Ordinal))
            { canonical = "mission"; missionKey = Expand(key)[9..]; }
            else if (Path(value) is { } path && aliases.ContainsKey(path.Split('.')[0])) canonical = Expand(path);
            // lib.one(constant, name) returns the constant; it only asserts that the constant exists.
            else if (value is LuaCall { Method: null, Arguments: { Form: LuaArgumentsForm.List, Values: [var constant, ..] } } one
                && Path(one.Target) is { } helper && Expand(helper) == "mission_lib.one" && Path(constant) is { } named
                && aliases.ContainsKey(named.Split('.')[0])) canonical = Expand(named);
            if (canonical is null) return;
            aliases[name] = canonical; expanded.Clear();
            if (canonical != "mission") continue;
            multipleMissions |= MissionVariable is not null && MissionVariable != name;
            MissionVariable = multipleMissions ? null : name;
            MissionKey = multipleMissions ? null : missionKey;
            MissionEnd = multipleMissions ? null : local.Values[i].Span.End;
        }
    }

    /** @return The one argument of `require(argument)`, or null. */
    private static LuaExpression? Require(LuaExpression value) => value is LuaCall
    {
        Target: LuaName { Name: "require" }, Method: null, Arguments: { Form: LuaArgumentsForm.List, Values: [var argument] }
    } ? argument : null;

    private void Invalidate(string name)
    {
        aliases.Remove(name); expanded.Clear(); multipleMissions = true; MissionVariable = null; MissionKey = null; MissionEnd = null;
    }

    /** @return A top-level name bound to exactly this canonical path, or null. */
    public string? Alias(string canonical) => aliases.FirstOrDefault(pair => pair.Value == canonical).Key;

    /** A dotted path with its alias root replaced by the canonical path; any other text comes back trimmed. */
    public string Expand(string expression)
    {
        if (expanded.TryGetValue(expression, out var known)) return known;
        string? path;
        try { path = Path(LuaScript.ParseExpression(expression)); }
        catch (InvalidDataException) { path = null; }
        string result = expression.Trim();
        if (path is not null)
        {
            int dot = path.IndexOf('.'); string root = dot < 0 ? path : path[..dot];
            result = aliases.TryGetValue(root, out var target) ? target + (dot < 0 ? "" : path[dot..]) : path;
        }
        return expanded[expression] = result;
    }

    /** The shortest source for a canonical path through the script's aliases; the path itself when none applies. */
    public string Shortest(string canonical)
    {
        string best = canonical;
        foreach (var (name, target) in aliases)
            if (canonical == target || canonical.StartsWith(target + ".", StringComparison.Ordinal))
            {
                string text = name + canonical[target.Length..];
                if (text.Length < best.Length) best = text;
            }
        return best;
    }

    /** A mission.* entry written through the script's mission variable; anything else throws. */
    public string Reference(MissionSdkReference reference) => MissionVariable is { } variable
        && reference.Expression.StartsWith("mission.", StringComparison.Ordinal)
        ? variable + reference.Expression[7..] : throw new NotSupportedException("This script has no plain mission SDK binding.");

    /** @return `a.b.c` for a name or a chain of member reads, or null. */
    private static string? Path(LuaExpression expression) => expression switch
    {
        LuaName name => name.Name,
        LuaMember member => Path(member.Target) is { } target ? target + "." + member.Name : null,
        _ => null
    };
}
