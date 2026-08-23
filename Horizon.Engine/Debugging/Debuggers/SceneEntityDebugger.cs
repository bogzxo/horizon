using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Horizon.Core;

using Egui;
using Egui.Containers;
using Egui.Widgets;

namespace Horizon.Engine.Debugging.Debuggers;

public class SceneEntityDebugger : DebuggerComponent
{
    private const float V_speed = 0.05f;
    public bool DebugInstance = true;

    public override void Initialize()
    {
        Name = "Scene Tree";
    }

    public override void RenderUi(Ui root)
    {
        if (!Visible)
            return;

        new Window(Name)
            .Show(root.Ctx, ui =>
            {
                Entity mainNode = DebugInstance
                    ? GameEngine.Instance
                    : GameEngine.Instance.SceneManager.CurrentInstance;

                ui.Label($"Total Entities: {mainNode.Children.Count}");
                ui.Label($"Total Components: {mainNode.Components.Count}");

                ui.Separator();

                DrawEntityTree(ui, mainNode);
            });
    }

    public override void Dispose()
    { }

    private void DrawEntityTree(Ui ui, Entity? entity)
    {
        if (entity is null)
            return;

        ui.Collapsing(entity.Name ?? "Unnamed Entity", innerUi =>
        {
            if (innerUi.Button("Remove Entity").Clicked)
            {
                entity?.Parent?.RemoveEntity(entity);
            }

            innerUi.Horizontal(row =>
            {
                row.Heading("Property");
                row.Heading("Value");
            });

            innerUi.Separator();

            // Draw Properties
            DrawProperties(innerUi, entity, 1);

            int collectionSize = entity!.Components.Count;

            // Draw Components
            for (int i = 0; i < collectionSize; i++)
            {
                if (collectionSize != entity!.Components.Count)
                    break; // detect and handle collection modification

                var component = entity!.Components[i];

                if (component == null)
                    continue;

                innerUi.Collapsing(component.Name ??= component.GetType().Name, compUi =>
                {
                    if (compUi.Button("Delete Component").Clicked)
                    {
                        entity.RemoveComponent(component);
                    }

                    DrawProperties(compUi, component, 1);
                });
            }

            // Recursively draw all entity children in a distinct visual hierarchy
            if (entity.Children.Count <= 0) return;
            innerUi.Separator();
            innerUi.Heading($"Children ({entity.Children.Count})");

            foreach (var ent in entity.Children)
            {
                DrawEntityTree(innerUi, ent);
            }

        });
    }

    public static void DrawProperties(Ui ui, object? component, int depth = 0, int maxDepth = 3)
    {
        if (component is null || depth > maxDepth)
        {
            ui.Label("Depth Limit Reached.");
            return;
        }

        Type type = component.GetType();
        var properties = type.GetProperties()
            .Where(p => p.GetIndexParameters().Length == 0)
            .ToArray();
        try
        {
            foreach (var property in properties)
            {
                if (property is null || property.IsCollectible)
                    continue;

                var value = property.GetValue(component);

                if (property.GetMethod?.IsStatic == true)
                {
                    continue; // Skip static properties
                }

                if (property.PropertyType.IsArray)
                {
                    DrawArrayProperty(ui, property.Name, (Array)value!);
                }
                else switch (property.PropertyType.IsGenericType)
                {
                    case true
                        when property.PropertyType.GetGenericTypeDefinition() == typeof(List<>):
                    {
                        if (value is null)
                            continue;

                        DrawListProperty(ui, property.Name, value);
                        break;
                    }
                    case true
                        when property.PropertyType.GetGenericTypeDefinition() == typeof(Dictionary<,>):
                    {
                        if (value is null)
                            continue;

                        DrawDictionaryProperty(ui, property.Name, value);
                        break;
                    }
                    default:
                    {
                        if (property.PropertyType.IsValueType)
                        {
                            if (
                                property.PropertyType.Namespace is null
                                || property.PropertyType.Namespace.StartsWith("System")
                            )
                                continue;

                            ui.Collapsing($"{property.Name} (Value Type)", innerUi =>
                            {
                                DrawProperties(innerUi, value, depth + 1);
                            });
                        }
                        else if (!property.CanWrite || value == null)
                        {
                            DrawPropertyRow(ui, property.Name, $"{value}");
                        }
                        else if (value is int intValue)
                        {
                            ui.Horizontal(row =>
                            {
                                row.Label(property.Name);
                                double dVal = intValue;
                                if (CustomDragValue(row, ref dVal))
                                {
                                    property.SetValue(component, (int)dVal);
                                }
                            });
                        }
                        else if (value is string stringValue)
                        {
                            DrawPropertyRow(ui, property.Name, $"\"{stringValue}\"");
                        }
                        else if (value is float floatValue)
                        {
                            ui.Horizontal(row =>
                            {
                                row.Label(property.Name);
                                double dVal = floatValue;
                                if (CustomDragValue(row, ref dVal))
                                {
                                    property.SetValue(component, (float)dVal);
                                }
                            });
                        }
                        else if (value is Vector2 vector2Value)
                        {
                            ui.Horizontal(row =>
                            {
                                row.Label(property.Name);
                                double x = vector2Value.X, y = vector2Value.Y;

                                bool changedX = CustomDragValue(row, ref x);
                                bool changedY = CustomDragValue(row, ref y);

                                if (changedX || changedY)
                                {
                                    property.SetValue(component, new Vector2((float)x, (float)y));
                                }
                            });
                        }
                        else if (value is Vector3 vector3Value)
                        {
                            ui.Horizontal(row =>
                            {
                                row.Label(property.Name);
                                double x = vector3Value.X, y = vector3Value.Y, z = vector3Value.Z;

                                bool changedX = CustomDragValue(row, ref x);
                                bool changedY = CustomDragValue(row, ref y);
                                bool changedZ = CustomDragValue(row, ref z);

                                if (changedX || changedY || changedZ)
                                {
                                    property.SetValue(component, new Vector3((float)x, (float)y, (float)z));
                                }
                            });
                        }
                        else if (value is Vector4 vector4Value)
                        {
                            ui.Horizontal(row =>
                            {
                                row.Label(property.Name);
                                double x = vector4Value.X, y = vector4Value.Y, z = vector4Value.Z, w = vector4Value.W;

                                bool changedX = CustomDragValue(row, ref x);
                                bool changedY = CustomDragValue(row, ref y);
                                bool changedZ = CustomDragValue(row, ref z);
                                bool changedW = CustomDragValue(row, ref w);

                                if (changedX || changedY || changedZ || changedW)
                                {
                                    property.SetValue(component, new Vector4((float)x, (float)y, (float)z, (float)w));
                                }
                            });
                        }
                        else if (value is bool boolValue)
                        {
                            if (ui.Checkbox(ref boolValue, property.Name).Changed)
                            {
                                property.SetValue(component, boolValue);
                            }
                        }
                        else
                        {
                            DrawPropertyRow(ui, property.Name, $"{GetFriendlyName(value)}");
                        }

                        break;
                    }
                }
            }
        }
        catch
        {
            // TODO: @bogz maybe handle this a little better man?
            ui.Label("EISH MY MAN");
            throw;
        }
    }

    private static void DrawPropertyRow(Ui ui, string propertyName, string propertyValue)
    {
        ui.Horizontal(row =>
        {
            row.Label(propertyName);
            row.Label(propertyValue);
        });
    }

    private static void DrawListProperty(Ui ui, string name, object listObj)
    {
        if (listObj is not IList list) return;
        if (list.Count < 1)
            return;

        ui.Collapsing($"{name} (List)", innerUi =>
        {
            innerUi.Horizontal(row =>
            {
                row.Label(name);
                row.Label("(List)");
            });

            for (int i = 0; i < list.Count; i++)
            {
                var element = list[i];

                if (element == null) continue;

                innerUi.Horizontal(row =>
                {
                    row.Label($"Element {i}");

                    switch (element)
                    {
                        case int intValue:
                            {
                                double dVal = intValue;
                                if (CustomDragValue(row, ref dVal))
                                {
                                    list[i] = (int)dVal;
                                }

                                break;
                            }
                        case float floatValue:
                            {
                                double dVal = floatValue;
                                if (CustomDragValue(row, ref dVal))
                                {
                                    list[i] = (float)dVal;
                                }

                                break;
                            }
                        case Vector2 vectorValue2:
                            {
                                double x = vectorValue2.X, y = vectorValue2.Y;
                                bool changedX = CustomDragValue(row, ref x);
                                bool changedY = CustomDragValue(row, ref y);
                                if (changedX || changedY)
                                {
                                    list[i] = new Vector2((float)x, (float)y);
                                }

                                break;
                            }
                        case Vector3 vectorValue3:
                            {
                                double x = vectorValue3.X, y = vectorValue3.Y, z = vectorValue3.Z;
                                bool changedX = CustomDragValue(row, ref x);
                                bool changedY = CustomDragValue(row, ref y);
                                bool changedZ = CustomDragValue(row, ref z);
                                if (changedX || changedY || changedZ)
                                {
                                    list[i] = new Vector3((float)x, (float)y, (float)z);
                                }

                                break;
                            }
                        case Vector4 vectorValue4:
                            {
                                double x = vectorValue4.X, y = vectorValue4.Y, z = vectorValue4.Z, w = vectorValue4.W;
                                bool changedX = CustomDragValue(row, ref x);
                                bool changedY = CustomDragValue(row, ref y);
                                bool changedZ = CustomDragValue(row, ref z);
                                bool changedW = CustomDragValue(row, ref w);
                                if (changedX || changedY || changedZ || changedW)
                                {
                                    list[i] = new Vector4((float)x, (float)y, (float)z, (float)w);
                                }

                                break;
                            }
                        case bool boolValue:
                            {
                                if (row.Checkbox(ref boolValue, "").Changed)
                                {
                                    list[i] = boolValue;
                                }

                                break;
                            }
                        default:
                            row.Label($"{GetFriendlyName(element)}");
                            break;
                    }

                    row.Separator();
                });
                innerUi.Separator();
            }
            innerUi.Separator();
        });
    }

    private static void DrawArrayProperty(Ui ui, string name, Array array)
    {
        if (array.Length < 1)
            return;

        ui.Horizontal(row =>
        {
            row.Label(name);
            row.Label("(Array)");
        });

        for (int i = 0; i < array.Length; i++)
        {
            var element = array.GetValue(i);

            if (element != null)
            {
                ui.Horizontal(row =>
                {
                    row.Label($"Element {i}");
                    DrawProperties(row, element, 1);
                });
            }
        }

        ui.Separator();
    }

    public static string GetFriendlyName(object? obj)
    {
        if (obj == null)
            return string.Empty;

        var type = obj.GetType();

        return $"{(type.IsClass ? type.Name : obj.ToString())}";
    }

    private static void DrawDictionaryProperty(Ui ui, string name, object dictionaryObj)
    {
        if (dictionaryObj is not IDictionary dictionary) return;

        if (dictionary.Count < 1)
            return;

        ui.Collapsing($"{name} (Dictionary)", innerUi =>
        {
            innerUi.Horizontal(row =>
            {
                row.Label("Key");
                row.Label("Value");
            });

            innerUi.Separator();

            foreach (DictionaryEntry entry in dictionary)
            {
                innerUi.Horizontal(row =>
                {
                    row.Label(entry.Key.ToString() ?? "null");
                    row.Label(GetFriendlyName(entry.Value));
                });
            }
        });
    }

    public static bool CustomDragValue(Ui ui, ref double value)
    {
        // Add the actual interactive EGui.NET DragValue element:
        return ui.Add(new DragValue<double>(ref value).Speed(V_speed)).Changed;
    }

    public override void UpdateState(float dt)
    { }

    public override void UpdatePhysics(float dt)
    { }
}