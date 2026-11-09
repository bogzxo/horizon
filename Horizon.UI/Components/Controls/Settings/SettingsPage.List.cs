namespace Horizon.UI.Components;

// The rows of a list of texts (UIFormKind.List, files_exclude and search_exclude in Hex) as a test sees them, what a row
// shows, the line being typed and where its buttons are. The row itself is in SettingRow.List
public partial class SettingsPage
{
    /// <summary>What a row of a list shows, the line being typed (-1 for none) and its box, null for a key with no such row. For a test.</summary>
    public (IReadOnlyList<string> Items, int Editing, TextBox? Box)? ListShown(string key) =>
        rowOf.TryGetValue(key, out SettingRow? row) && row.Setting.Kind == UIFormKind.List ? (row.ItemsShown, row.EditingItem, row.ItemBoxShown) : null;

    /// <summary>Where a row of a list has its add button, and every line's pencil and bin, null for a key with no such row. For a test to press with the pointer.</summary>
    public (UIRect Add, UIRect[] Edits, UIRect[] Removes)? ListBounds(string key) =>
        rowOf.TryGetValue(key, out SettingRow? row) && row.Setting.Kind == UIFormKind.List ? row.ItemBounds : null;
}
