using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.UI;

/// <summary>
/// Reads the saved control sizing. A value that is not true or false reads as
/// Standard instead of failing the whole of ui.json, so one damaged key does
/// not cost the user every other window preference.
/// </summary>
public sealed class LocalTouchControlsConverter : JsonConverter<bool>
{
    public override bool HandleNull => true;

    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False) return reader.GetBoolean();
        reader.Skip();
        return false;
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}

/// <summary>
/// Reads the saved docking choice. Anything but true or false, a missing key
/// included, reads as no choice, so a damaged value lets a skin's pop-out
/// default apply instead of failing the whole of ui.json. No choice is
/// written as null.
/// </summary>
public sealed class LocalMixerDockedConverter : JsonConverter<bool?>
{
    public override bool HandleNull => true;

    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False) return reader.GetBoolean();
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is { } docked) writer.WriteBooleanValue(docked);
        else writer.WriteNullValue();
    }
}

/// <summary>
/// Reads the saved size of the mixer's own window. Anything but one of
/// <see cref="MixerScale.Choices"/>, a missing key included, reads as no
/// choice, which draws the panel at the skin's own size, instead of failing
/// the whole of ui.json. No choice is written as null.
/// </summary>
public sealed class LocalMixerScaleConverter : JsonConverter<string?>
{
    public override bool HandleNull => true;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return MixerScale.Known(reader.GetString());
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (MixerScale.Known(value) is { } known) writer.WriteStringValue(known);
        else writer.WriteNullValue();
    }
}
