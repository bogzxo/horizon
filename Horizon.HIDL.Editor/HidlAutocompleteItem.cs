using System;
using AutocompleteMenuNS;

namespace Horizon.HIDL.Editor
{
    public class HidlAutocompleteItem : AutocompleteItem
    {
        public string ItemType { get; set; }
        public string? ParentObject { get; set; }
        public string SimpleName { get; set; }

        public HidlAutocompleteItem(string text, string itemType, string? parentObject = null, string? toolTip = null)
            : base(string.IsNullOrEmpty(parentObject) ? text : $"{parentObject}.{text}")
        {
            SimpleName = text;
            ItemType = itemType;
            ParentObject = parentObject;

            ImageIndex = GetImageIndex(itemType, parentObject);

            MenuText = string.IsNullOrEmpty(parentObject)
                ? $"{text} : {itemType}"
                : $"{text} : {itemType}";

            ToolTipTitle = string.IsNullOrEmpty(parentObject) ? text : $"{parentObject}.{text}";
            ToolTipText = toolTip ?? (string.IsNullOrEmpty(parentObject)
                ? $"{itemType} {text}"
                : $"{itemType} property {text} of {parentObject}");
        }

        private static int GetImageIndex(string itemType, string? parentObject)
        {
            // Sort by Variable, Constant, Parameter thjen Primitives
            if (itemType == "Keyword") return 0;
            if (itemType == "Function" || itemType == "Native Function") return 1;
            if (!string.IsNullOrEmpty(parentObject)) return 3;
            if (itemType == "Object" || itemType.StartsWith("Vector", StringComparison.OrdinalIgnoreCase)) return 4;
            return 2; 
        }

        public override CompareResult Compare(string fragmentText)
        {
            if (string.IsNullOrEmpty(fragmentText))
                return CompareResult.Visible;

            if (!string.IsNullOrEmpty(ParentObject))
            {
                // Property item (e.g. Text = "user.name", ParentObject = "user")
                if (Text.StartsWith(fragmentText, StringComparison.OrdinalIgnoreCase))
                    return CompareResult.VisibleAndSelected;

                if (Text.Contains(fragmentText, StringComparison.OrdinalIgnoreCase))
                    return CompareResult.Visible;

                return CompareResult.Hidden;
            }
            else
            {
                // Top-level item (e.g. Text = "user", ParentObject = null)
                if (fragmentText.Contains("."))
                    return CompareResult.Hidden;

                if (Text.StartsWith(fragmentText, StringComparison.OrdinalIgnoreCase))
                    return CompareResult.VisibleAndSelected;

                if (Text.Contains(fragmentText, StringComparison.OrdinalIgnoreCase))
                    return CompareResult.Visible;

                return CompareResult.Hidden;
            }
        }
    }
}