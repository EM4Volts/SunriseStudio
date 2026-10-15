using static SunriseStudio.Missions.LuaText;

namespace SunriseStudio.Missions;

/**
 * Writes typed call edits as span replacements; every other byte of the script stays as it was.
 * Each edit returns new source that still parses, or throws.
 */
public static class MissionApiWriter
{
    // Reach follows at most this many fields; placeholders follow at most this many levels and maker calls.
    private const int ReachDepth = 4;

    /** Replaces a present parameter's value. */
    public static string SetValue(MissionApiNode node, string path, string expression)
    {
        var parameter = Require(node, path);
        if (parameter.Value is not { } value) throw new InvalidOperationException(parameter.Label + " is absent; add it first.");
        CheckExpression(expression);
        string source = node.Analysis.Source;
        if (expression == value.Span.Read(source)) return source;
        return Checked(Edit(source, value.Span, Lines(source, expression)));
    }

    /** Adds an absent argument, table field or atom field with its default or a valid placeholder. */
    public static string Add(MissionApiNode node, string path, string? expression = null)
    {
        var parameter = Require(node, path);
        if (parameter.Present) return node.Analysis.Source;
        string value = expression ?? Placeholder(node, parameter.Spec, node.Call.Names);
        CheckExpression(value);
        string source = node.Analysis.Source;
        int dot = path.LastIndexOf('.');
        if (dot < 0)
        {
            if (!node.Member.Named) return AddPositional(node, parameter, value);
            if (node.Table is { } table) return Checked(InsertField(source, table, parameter.Spec.Name + " = " + value));
            var arguments = node.Call.Call.Arguments;
            if (arguments.Values.Count > 0) throw new NotSupportedException("This call passes a value that is not a table; edit it as Lua.");
            return Checked(Edit(source, arguments.Span, "{" + parameter.Spec.Name + " = " + value + "}"));
        }
        var owner = Require(node, path[..dot]);
        if (!owner.Present) throw new InvalidOperationException("Add " + owner.Label + " first.");
        if (owner.Value is not LuaTable parent) throw new NotSupportedException(owner.Label + " is Lua code; add the field in Lua.");
        return Checked(InsertField(source, parent, parameter.Spec.Name + " = " + value));
    }

    /** Removes an optional argument, a table field or a list item with its separator. */
    public static string Remove(MissionApiNode node, string path)
    {
        var parameter = Require(node, path);
        if (!parameter.Present) return node.Analysis.Source;
        if (!parameter.Spec.Optional && !parameter.Item) throw new InvalidOperationException(parameter.Label + " is required.");
        string source = node.Analysis.Source;
        if (parameter.Field is { } field)
        {
            var table = Owner(node, path) ?? throw new InvalidOperationException("The table of this field changed.");
            return Checked(RemoveField(source, table, field));
        }
        var values = node.Call.Call.Arguments.Values;
        int index = values.ToList().FindIndex(value => ReferenceEquals(value, parameter.Value));
        if (index != values.Count - 1) throw new NotSupportedException("Only the last positional argument can be removed.");
        var span = index == 0 ? node.Call.Call.Arguments.Inner : LuaSpan.FromBounds(values[index - 1].Span.End, values[index].Span.End);
        return Checked(Edit(source, span, ""));
    }

    /** Appends a placeholder item to a list or to the array part of a table. */
    public static string AddItem(MissionApiNode node, string path)
    {
        var parameter = Require(node, path);
        var element = parameter.Spec.Element ?? throw new InvalidOperationException(parameter.Label + " has no list entries.");
        if (parameter.Value is not LuaTable table) throw new NotSupportedException(parameter.Label + " is Lua code; add the entry in Lua.");
        if (table.Fields.Count(field => field.Positional) >= parameter.Spec.MaxCount)
            throw new InvalidOperationException($"{parameter.Label} takes at most {parameter.Spec.MaxCount} entries.");
        return Checked(InsertField(node.Analysis.Source, table, Placeholder(node, element, node.Call.Names)));
    }

    /** Lua source for a typed choice: an entry of a generated table, written through the script's aliases. */
    public static string ReferenceText(MissionApiNode node, MissionSdkReference reference) => node.Analysis.Bindings.Shortest(reference.Expression);

    /** A slot handle for a generated entry, looked up the way the schema says, such as context:slot(Slot.X). */
    public static string HandleText(MissionApiNode node, MissionApiParameter parameter, MissionSdkReference reference)
    {
        var lookup = node.Analysis.Api.Schema.LookupFor(parameter.Spec.Ref!) ?? throw new InvalidOperationException("No lookup makes this handle.");
        string receiver = parameter.Value is LuaCall { Target: var target } call && node.Analysis.Find(call)?.Member == lookup
            ? target.Span.Read(node.Analysis.Source)
            : Reach(node.Analysis.Api.Schema, node.Call.Names, lookup.Receiver) ?? throw new InvalidOperationException($"No {lookup.Receiver} is in scope here.");
        return LookupCall(node.Analysis, lookup, receiver, reference);
    }

    /** Lua source for an enum entry: the current collection path with another entry, or a path from a name in scope. */
    public static string ChoiceText(MissionApiNode node, MissionApiParameter parameter, string entry)
    {
        var schema = node.Analysis.Api.Schema;
        if (parameter.Widget == MissionApiWidget.Atom) return LuaScript.Quote(entry);
        if (parameter.Literal && parameter.Value is LuaMember member) return member.Target.Span.Read(node.Analysis.Source) + "." + entry;
        string collection = schema.CollectionFor(parameter.Spec.Ref!)?.Name
            ?? throw new InvalidOperationException($"No collection serves {parameter.Spec.Ref}.");
        return (Reach(schema, node.Call.Names, collection) ?? throw new InvalidOperationException($"No path to {collection} is in scope here."))
            + "." + entry;
    }

    /** Sets the kind of an atom; its other fields stay. */
    public static string SetAtomKind(MissionApiNode node, string path, string kind)
    {
        var parameter = Require(node, path);
        if (parameter.Value is not LuaTable atom) throw new NotSupportedException(parameter.Label + " is Lua code.");
        string text = ChoiceText(node, parameter, kind), key = node.Analysis.Api.Schema.KindField(parameter.Spec.Ref!);
        string source = node.Analysis.Source;
        return atom.Field(key) is { } field ? Checked(Edit(source, field.Value, text)) : Checked(InsertField(source, atom, key + " = " + text));
    }

    /**
     * A valid value of a kind for new code: the default, the first matching generated entry, or a handle built from a name in scope.
     * @param names The typed names in scope where the value goes.
     */
    public static string Placeholder(MissionApiNode node, MissionApiValue spec, IReadOnlyDictionary<string, MissionApiType> names) =>
        Placeholder(node.Analysis, spec, names, node.Receiver, 0);

    /** @param depth Fields, list levels and maker calls already followed; a handle stops at ReachDepth. */
    private static string Placeholder(MissionApiAnalysis analysis, MissionApiValue spec, IReadOnlyDictionary<string, MissionApiType> names,
        MissionApiType? receiver, int depth)
    {
        if (spec.Default is { } initial) return initial;
        var schema = analysis.Api.Schema;
        double low = spec.Minimum ?? double.MinValue, high = spec.Maximum ?? double.MaxValue;
        string number = MissionApiSchema.NumberText(Math.Clamp(0, low, high));
        switch (spec.Kind)
        {
            case "boolean": return "false";
            case "integer" or "selector" or "mission_row" when spec.Ref is { } table && FirstEntry(analysis, table, spec, receiver) is { } entry:
                return analysis.Bindings.Shortest(entry.Expression);
            case "integer" or "number": return number;
            case "decimal_string": return LuaScript.Quote(number);
            case "string" or "name": return LuaScript.Quote(new string('x', Math.Max(1, spec.MinLength ?? 1)));
            case "scalar": return "true";
            case "handle" when spec.Ref is { } handle && depth < ReachDepth:
                if (schema.LookupFor(handle) is { } lookup
                    && FirstEntry(analysis, lookup.Arguments[0].Ref!, spec, null) is { } slot
                    && Reach(schema, names, lookup.Receiver) is { } owner)
                    return LookupCall(analysis, lookup, owner, slot);
                return Make(analysis, names, handle, depth + 1) ?? "nil";
            case "enum" when spec.Ref is { } name:
                var collection = schema.CollectionFor(name);
                if (collection is not null && schema.Enum(name)?.Entries.FirstOrDefault() is { } first && Reach(schema, names, collection.Name) is { } path)
                    return path + "." + first.Name;
                return schema.Enum(name) is { } values && schema.ReceiverForClass(values.LuaClass) is { } element
                    ? Make(analysis, names, element.Name, depth + 1) ?? "nil" : "nil";
            case "list":
                var items = Enumerable.Range(0, spec.MinCount ?? 0).Select(_ => Placeholder(analysis, spec.Element!, names, receiver, depth + 1));
                return "{" + string.Join(", ", items) + "}";
            case "table":
                return "{" + string.Join(", ", spec.Fields.Where(field => !field.Optional).Select(field =>
                    field.Name + " = " + Placeholder(analysis, field, names, receiver, depth + 1))) + "}";
            case "atom" when spec.Ref is { } kinds && schema.Enum(kinds)?.Entries.FirstOrDefault() is { } kind:
                return "{" + string.Join(", ", kind.Fields.Where(field => !field.Optional).Select(field => field.Name + " = "
                    + (field.Ref == kinds ? LuaScript.Quote(kind.Name) : Placeholder(analysis, field, names, receiver, depth + 1)))) + "}";
            case "function": return "function() end";
            default: return "nil";
        }
    }

    /**
     * New call source for a member on a receiver expression, with its required arguments filled in.
     * @param type The receiver's type, so arguments scoped to it pick its own entries; null when not known.
     */
    public static string CallText(MissionApiAnalysis analysis, MissionApiMember member, string receiver,
        IReadOnlyDictionary<string, MissionApiType> names, MissionApiType? type = null) => CallText(analysis, member, receiver, names, type, 0);

    private static string CallText(MissionApiAnalysis analysis, MissionApiMember member, string receiver,
        IReadOnlyDictionary<string, MissionApiType> names, MissionApiType? type, int depth)
    {
        string call = receiver + member.Separator + member.Name;
        var required = member.Arguments.Where(value => !value.Optional).ToArray();
        if (member.Named)
            return call + "{" + string.Join(", ", required.Select(value => value.Name + " = " + Placeholder(analysis, value, names, type, depth))) + "}";
        return call + "(" + string.Join(", ", required.Select(value => Placeholder(analysis, value, names, type, depth))) + ")";
    }

    /**
     * Receiver source for a new call: a typed name in scope, a lookup of the first entry the member's slot gate accepts,
     * or a path or maker call from a name in scope. Null when nothing in scope reaches the receiver.
     */
    public static (string Text, MissionApiType Type)? ReceiverText(MissionApiAnalysis analysis, MissionApiMember member,
        IReadOnlyDictionary<string, MissionApiType> names)
    {
        var schema = analysis.Api.Schema;
        foreach (var (name, type) in names.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (type.Receiver == member.Receiver && (member.Gate is not { } required || type.SlotType == required.SlotType)) return (name, type);
        if (schema.LookupFor(member.Receiver) is { } lookup && Reach(schema, names, lookup.Receiver) is { } owner)
        {
            var entries = analysis.Api.Table(lookup.Arguments[0].Ref!).AcceptedBy(member.Gate);
            if (entries.FirstOrDefault() is { } entry)
                return (LookupCall(analysis, lookup, owner, entry), new MissionApiType(member.Receiver) { Identity = entry });
            // No slot of the gate's type exists here, so no receiver is valid.
            if (member.Gate is not null) return null;
        }
        if (Reach(schema, names, member.Receiver) is { } path) return (path, new MissionApiType(member.Receiver));
        return Make(analysis, names, member.Receiver, 1) is { } made ? (made, new MissionApiType(member.Receiver)) : null;
    }

    /** A callback field for the returned table, with the schema's parameter names. */
    public static string CallbackText(MissionApiSchema schema, string callback)
    {
        var parameters = schema.EventForCallback(callback) is not null ? schema.Program.EventParameters : schema.Program.StartParameters;
        return callback + " = function(" + string.Join(", ", parameters) + ")\nend";
    }

    /** Source that reaches a receiver from a typed name in scope through handle fields, such as context.sdk; null when none does. */
    public static string? Reach(MissionApiSchema schema, IReadOnlyDictionary<string, MissionApiType> names, string receiver)
    {
        var queue = new Queue<(string Receiver, string Text, int Depth)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, type) in names.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (!type.EnumTable && type.Receiver != MissionApiType.EventReceiver) queue.Enqueue((type.Receiver, name, 0));
        while (queue.Count > 0)
        {
            var (current, text, depth) = queue.Dequeue();
            if (current == receiver) return text;
            if (!seen.Add(current) || depth >= ReachDepth) continue;
            foreach (var member in schema.Receiver(current)?.Members ?? [])
                if (member is { Callable: false, Type: { Kind: "handle", Ref: { } next } }) queue.Enqueue((next, text + "." + member.Name, depth + 1));
        }
        return null;
    }

    /**
     * A maker or lookup call that returns this handle, made on a receiver in scope or on one a lookup returns.
     * Requests and updates are never used: a placeholder must not change the mission.
     */
    private static string? Make(MissionApiAnalysis analysis, IReadOnlyDictionary<string, MissionApiType> names, string handle, int depth)
    {
        var schema = analysis.Api.Schema;
        if (depth >= ReachDepth) return null;
        foreach (var member in schema.Members.Where(value => value.Kind is "maker" or "lookup"
            && value.Returns is [{ Kind: "handle" } returned] && returned.Ref == handle))
        {
            if (member.Arguments.Any(value => !value.Optional && value.Kind == "handle")) continue;
            string? owner = Reach(schema, names, member.Receiver);
            if (owner is null && schema.LookupFor(member.Receiver) is { } lookup && Reach(schema, names, lookup.Receiver) is { } root
                && analysis.Api.Table(lookup.Arguments[0].Ref!).AcceptedBy(member.Gate).FirstOrDefault() is { } entry)
                owner = LookupCall(analysis, lookup, root, entry);
            if (owner is not null) return CallText(analysis, member, owner, names, null, depth);
        }
        return null;
    }

    private static MissionSdkReference? FirstEntry(MissionApiAnalysis analysis, string table, MissionApiValue spec, MissionApiType? receiver)
    {
        var entries = analysis.Api.Table(table).AcceptedBy(spec.SlotGate ?? spec.Element?.SlotGate);
        if (spec.Scope == "receiver" && receiver?.Identity?.Key is { } owner) entries = entries.Where(entry => entry.Owner.Length == 0 || entry.Owner == owner);
        return entries.FirstOrDefault();
    }

    /** A lookup call on an owner for one generated entry, such as context:slot(Slot.X). */
    private static string LookupCall(MissionApiAnalysis analysis, MissionApiMember lookup, string owner, MissionSdkReference entry) =>
        owner + lookup.Separator + lookup.Name + "(" + analysis.Bindings.Shortest(entry.Expression) + ")";

    private static MissionApiParameter Require(MissionApiNode node, string path) => node.Find(path)
        ?? throw new InvalidOperationException("The call has no parameter " + path + ".");

    /** The table that holds a field parameter: the call's table, a nested table or a list. */
    private static LuaTable? Owner(MissionApiNode node, string path)
    {
        int split = Math.Max(path.LastIndexOf('.'), path.LastIndexOf('['));
        if (split < 0) return node.Table;
        return node.Find(path[..split])?.Value as LuaTable;
    }

    private static string AddPositional(MissionApiNode node, MissionApiParameter parameter, string value)
    {
        var arguments = node.Call.Call.Arguments;
        int index = node.Parameters.ToList().FindIndex(item => ReferenceEquals(item, parameter));
        if (arguments.Form != LuaArgumentsForm.List || index != arguments.Values.Count)
            throw new NotSupportedException("Add positional arguments in order, after the last one present.");
        string source = node.Analysis.Source;
        return Checked(arguments.Values.Count == 0 ? Edit(source, arguments.Inner, value)
            : Edit(source, new(arguments.Values[^1].Span.End, 0), ", " + value));
    }

    private static string Checked(string source)
    {
        LuaScript.Parse(source);
        return source;
    }
}
