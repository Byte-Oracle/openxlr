using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class InsertParameterStateTests
{
    [Theory]
    [InlineData("lv2", false)]
    [InlineData("lv2", true)]
    [InlineData("clap", false)]
    [InlineData("clap", true)]
    [InlineData("vst3", false)]
    [InlineData("vst3", true)]
    public async Task ARecalledChainShowsDefaultsForControlsWithoutSavedValues(string kind, bool missingParams)
    {
        await using var client = new DaemonClient();
        var owner = new InsertsViewModel(client, "xlr1");
        var initial = new JsonObject { ["gain"] = .9, ["enabled"] = 0.0, ["voices"] = 9.0 };
        owner.Apply(Chain(kind, initial));
        InsertViewModel insert = Assert.Single(owner.Items);
        InsertParamViewModel[] controls = Controls(insert);
        owner.Apply(Chain(kind, initial.DeepClone()));
        Assert.Equal(new[] { .9, 0, 9 }, controls.Select(p => p.Value));

        // An A/B slot stored before any control edits can omit every value
        // while retaining the insert id and its open controls window.
        owner.Apply(Chain(kind, missingParams ? null : new JsonObject()));

        Assert.Same(insert, Assert.Single(owner.Items));
        Assert.Equal(controls, insert.Params);
        Assert.Equal(new[] { .25, 1, 3 }, controls.Select(p => p.Value));
        Assert.True(controls[1].On);
        Assert.Empty(PayloadParams(insert));
    }

    [Theory]
    [InlineData("lv2")]
    [InlineData("clap")]
    [InlineData("vst3")]
    public async Task RecalledExplicitValuesAndARecentLocalEditKeepTheirMeaning(string kind)
    {
        await using var client = new DaemonClient();
        var owner = new InsertsViewModel(client, "xlr1");
        owner.Apply(Chain(kind, new JsonObject { ["gain"] = .9, ["enabled"] = 0.0, ["voices"] = 9.0 }));
        InsertViewModel insert = Assert.Single(owner.Items);
        InsertParamViewModel[] controls = Controls(insert);
        owner.Apply(Chain(kind, new JsonObject { ["gain"] = .6, ["enabled"] = 0.0, ["voices"] = 9.0 }));
        string key = owner.ParameterKey(insert.Id, "gain");
        SliderSync.Touch(key);
        try
        {
            owner.Apply(Chain(kind, new JsonObject { ["voices"] = 7.0 }));

            Assert.Equal(new[] { .6, 1, 7 }, controls.Select(p => p.Value));
            Assert.Equal(.6, PayloadParams(insert)["gain"]!.GetValue<double>());
            Assert.Equal(7, PayloadParams(insert)["voices"]!.GetValue<double>());
            Assert.Equal(2, PayloadParams(insert).Count);
        }
        finally { SliderSync.Forget(key); }

        owner.Apply(Chain(kind, new JsonObject()));
        Assert.Equal(new[] { .25, 1, 3 }, controls.Select(p => p.Value));
        Assert.Empty(PayloadParams(insert));
    }

    private static InsertParamViewModel[] Controls(InsertViewModel insert)
    {
        InsertParamViewModel[] controls =
        [
            new(insert, "gain", "Gain", 0, 1, .25, false, false, false),
            new(insert, "enabled", "Enabled", 0, 1, 1, true, false, false),
            new(insert, "voices", "Voices", 1, 16, 3, false, true, false),
        ];
        foreach (InsertParamViewModel control in controls) insert.Params.Add(control);
        return controls;
    }

    private static JsonObject PayloadParams(InsertViewModel insert)
        => JsonSerializer.SerializeToNode(insert.ToPayload())!["params"]!.AsObject();

    private static JsonArray Chain(string kind, JsonNode? parameters)
    {
        var insert = new JsonObject { ["id"] = "defaults", ["kind"] = kind, ["plugin"] = "urn:test:defaults" };
        if (parameters is not null) insert["params"] = parameters;
        return new JsonArray(new JsonObject { ["insert"] = insert });
    }
}
