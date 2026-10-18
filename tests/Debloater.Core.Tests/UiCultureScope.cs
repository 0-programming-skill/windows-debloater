using System.Globalization;

namespace Debloater.Core.Tests;

/// <summary>Sets <see cref="CultureInfo.CurrentUICulture"/> for the current test and restores it on dispose.</summary>
internal sealed class UiCultureScope : IDisposable
{
    private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

    public UiCultureScope(string culture) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);

    public void Dispose() => CultureInfo.CurrentUICulture = _previous;
}
