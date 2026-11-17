using System.Text.Json;

namespace Balsoft.Hive.BDC.App.Services;

/// <summary>
/// Whether the settings form differs from what was last loaded or saved. The form is compared as the JSON it would send,
/// so a change anywhere (a company, a destination, an inbound flow) counts, and a value put back as it was does not.
/// </summary>
public sealed class UnsavedChanges
{
    private string? _remembered;

    public void Remember(object form) => _remembered = JsonSerializer.Serialize(form, form.GetType());

    /// <summary>True when the form differs or a password was typed; false while nothing was loaded, so nothing can be lost.</summary>
    public bool Differ(object form, bool passwordTyped)
        => _remembered is not null && (passwordTyped || JsonSerializer.Serialize(form, form.GetType()) != _remembered);
}