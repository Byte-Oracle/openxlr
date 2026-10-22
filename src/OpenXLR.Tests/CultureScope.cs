using System.Globalization;

namespace OpenXLR.Tests;

/// <summary>
/// Formats numbers as the named culture does until disposed, then puts the
/// previous culture back.
///
/// The suite runs under whatever locale the machine has, and a desktop set to
/// Italian, Spanish or German writes 0.1 as "0,1". A test that checks text
/// the window shows pins the culture it describes; a test that checks
/// protocol text pins one with a decimal comma, so a locale leaking into the
/// daemon's messages fails on every machine.
/// </summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

    public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

    public void Dispose() => CultureInfo.CurrentCulture = _previous;
}
