using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Hexoer.ViewModels;

namespace Hexoer;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var name = param.GetType().FullName!
            .Replace("ViewModel", "View", StringComparison.Ordinal)
            .Replace(".ViewModels.", ".Views.", StringComparison.Ordinal);
        var type = Type.GetType(name);

        // Fallback: try assembly-qualified lookup by simple rename
        if (type is null)
        {
            var simple = param.GetType().Name.Replace("ViewModel", "View", StringComparison.Ordinal);
            type = Type.GetType($"Hexoer.Views.{simple}, Hexoer");
        }

        if (type is not null)
            return (Control)Activator.CreateInstance(type)!;

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
