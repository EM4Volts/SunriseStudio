using SunriseStudio.Editor;
using SunriseStudio.Missions;
using SunriseStudio.Projects;
using static MissionUi;

/** Typed API nodes in the Mission Graph: every widget action edits one span, and the palettes write valid calls. */
internal static class ApiNodeUiChecks
{
    private const string Source = "local missions = require(\"missions\")\n"
        + "local mission = require(missions.MISSION_LIGHTS)\n"
        + "local Slot = mission.Slot\n"
        + "return {\n"
        + "    on_start = function(context, state)\n"
        + "        context:slot(Slot.M_DIRECTIVE_SENSOR):set_directive{directive = mission.Directive.REPAIR_THE_CONVERTERS, state = 0}\n"
        + "        context:slot(Slot.GEN1_PISTON_DEVICE):transition{transition = context.sdk.device_transitions.open}\n"
        + "    end,\n"
        + "}\n";

    /** @param schemaPath A mission_api.json; the installation may not have one yet. */
    public static int Run(string game, string schemaPath, string? capture)
    {
        MissionApiSchema schema = null!;
        string file = "", report = "";
        int phase = 0, drawn = 0;
        return MissionUi.Run("api-ui", temporary =>
        {
            schema = MissionApiSchema.Load(schemaPath);
            var project = StudioProject.Create(Path.Combine(temporary, "project"), "Typed nodes");
            file = MissionProject.Save(project, "api_nodes/api_nodes.lua", Source);
            return new(Game: game, Project: project.ManifestPath, Warp: true, Smoke: true,
                Frames: 40, Page: "Mission Graph", MissionScript: file, Capture: capture);
        }, (form, page) =>
        {
            if (!page.CanSave || page.Document is null) return;
            if (phase == 0)
            {
                var catalog = new MissionScriptCatalog(game);
                var entry = (MissionScriptEntry)Field(page, "script")!;
                Set(page, "apiSchema", schema);
                Set(page, "references", catalog.ReadReferences(entry, schema.MissionTables));
                Invoke(page, "Refresh");
                report = Edits(page, schema);
                phase++;
            }
            else if (phase < 6) { Check(((string)Field(page, "status")!).Length == 0, "The typed inspector failed to draw: " + Field(page, "status")); drawn++; phase++; }
        }, (_, _) =>
        {
            Check(phase == 6 && drawn == 5, $"Typed node UI did not finish: phase={phase}.");
            Check(File.ReadAllText(file) == Source, "The typed node checks saved the script.");
        }, () => "mission_api_ui " + report + $" capture={capture}");
    }

    /** Each widget action, checked as one span replacement of the source before it. */
    private static string Edits(MissionGraphPage page, MissionApiSchema schema)
    {
        var view = (MissionScriptGraph)Field(page, "scriptGraph")!;
        var start = view.Nodes.Single(node => node.Key == MissionScriptGraph.Root + "/field/on_start");
        var body = view.Body(start).Nodes;
        var directive = body.Single(node => node.ApiCall?.Member?.Name == "set_directive");
        var transition = body.Single(node => node.ApiCall?.Member?.Name == "transition");
        Check(directive.Kind == MissionScriptNodeKind.ApiCall && directive.Title == "Set directive"
            && transition.ApiCall!.Receiver!.SlotType == 23, "The calls did not open as typed nodes.");
        int Ordinal(MissionScriptNode node) => node.ApiCalls.ToList().IndexOf(node.ApiCall!);
        Invoke(page, "OpenGroup", "body:" + start.Key);
        Set(page, "selected", directive.Key);

        string Step(string before, Func<bool> action, string removed, string added, string what)
        {
            Check(action(), what + " was refused: " + Field(page, "status"));
            string after = page.Document!.Source;
            int at = before.IndexOf(removed, StringComparison.Ordinal);
            Check(at >= 0 && after == before[..at] + added + before[(at + removed.Length)..], $"{what} changed more than its span:\n{after}");
            return after;
        }

        string source = page.Document!.Source;
        source = Step(source, () => (bool)Invoke(page, "PickApiChoice", transition.Key, Ordinal(transition), "transition", "close")!,
            "device_transitions.open", "device_transitions.close", "An enum change");
        var references = (IReadOnlyList<MissionSdkReference>)Field(page, "references")!;
        var other = references.First(value => value.Table == "mission.Directive" && value.Key != "REPAIR_THE_CONVERTERS");
        source = Step(source, () => (bool)Invoke(page, "PickApiReference", directive.Key, Ordinal(directive), "directive", other)!,
            "mission.Directive.REPAIR_THE_CONVERTERS", "mission.Directive." + other.Key, "A mission-row pick");
        source = Step(source, () => (bool)Invoke(page, "ApplyApiValue", directive.Key, Ordinal(directive), "state", "2")!,
            "state = 0", "state = 2", "A numeric field");
        Invoke(page, "SetApiLuaMode", directive.Key, Ordinal(directive), "state", true);
        Check(page.Document!.Source == source && ((HashSet<string>)Field(page, "apiLua")!).Count == 1, "Lua mode changed the source.");
        source = Step(source, () => (bool)Invoke(page, "ApplyApiValue", directive.Key, Ordinal(directive), "state", "1 + 1")!,
            "state = 2", "state = 1 + 1", "A Lua expression");
        var analysis = ((MissionScriptGraph)Field(page, "scriptGraph")!).Api!;
        var typed = MissionApiNode.Build(analysis, analysis.Calls.Single(call => call.Member?.Name == "set_directive"));
        Check(typed.Find("state") is { Literal: false, Present: true }, "A computed value did not switch the parameter to Lua mode.");
        var navpoint = typed.Find("navpoint")!.Choices[0];
        source = Step(source, () => (bool)Invoke(page, "AddApiArgument", directive.Key, Ordinal(directive), "navpoint")!,
            "state = 1 + 1}", "state = 1 + 1, navpoint = context:slot(Slot." + navpoint.Key + ")}", "Adding an optional argument");
        source = Step(source, () => (bool)Invoke(page, "RemoveApiArgument", directive.Key, Ordinal(directive), "state")!,
            ", state = 1 + 1", "", "Removing an optional argument");
        source = Step(source, () => (bool)Invoke(page, "AddApiCallback", "on_event_scene_output")!,
            "    end,\n}", "    end,\n    on_event_scene_output = function(context, state, event)\n    end,\n}", "A callback from the palette");
        var variable = schema.Members.Single(member => member is { Receiver: "context", Name: "set_variable" });
        string scope = start.Key + "/scope/body";
        source = Step(source, () => (bool)Invoke(page, "AddApiMember", scope, variable)!,
            "device_transitions.close}\n", "device_transitions.close}\n        context:set_variable(\"x\", true)\n", "A member from the palette");
        Check(page.CanUndo, "Typed edits did not enter undo.");
        for (int i = 0; i < 8; i++) page.Undo();
        Check(page.Document!.Source == Source, "Undo did not restore the source.");
        var current = (MissionScriptGraph)Field(page, "scriptGraph")!;
        var refreshed = current.Body(current.Node(start.Key)).Nodes.Single(node => node.ApiCall?.Member?.Name == "set_directive");
        Set(page, "selected", refreshed.Key);
        return "enum=pass reference=pass number=pass lua_mode=pass add=pass remove=pass callback=pass member=pass undo=pass";
    }
}
