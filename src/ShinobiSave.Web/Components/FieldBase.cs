using Microsoft.AspNetCore.Components;
using ShinobiSave.Core;

namespace ShinobiSave.Web.Components;

/// <summary>A labelled editor bound to one field of a struct in the save.</summary>
public abstract class FieldBase : ComponentBase
{
    [CascadingParameter] public EditorState S { get; set; } = default!;
    [Parameter] public string Label { get; set; } = "";
    [Parameter] public string? StructName { get; set; }
    [Parameter] public List<GvasProperty> Props { get; set; } = default!;
    [Parameter] public string Field { get; set; } = "";

    protected FieldSlot Slot => S.Slot(StructName, Props, Field);

    protected void Set(object? value) => S.Set(Slot, value);
}
