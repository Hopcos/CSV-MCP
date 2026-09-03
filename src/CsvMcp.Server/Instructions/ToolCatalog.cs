using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace CsvMcp.Server.Instructions;

/// <summary>Describes one tool parameter as extracted from the tool method signature.</summary>
/// <param name="Name">Parameter name.</param>
/// <param name="TypeName">Human-readable JSON type name (e.g. <c>string[]</c>).</param>
/// <param name="IsRequired"><c>true</c> when the parameter has no default value.</param>
/// <param name="Description">Parameter description from a <see cref="DescriptionAttribute"/>.</param>
public sealed record ToolParameterDescriptor(string Name, string TypeName, bool IsRequired, string? Description);

/// <summary>Describes one tool as declared by its <see cref="McpServerToolAttribute"/> and XML/description metadata.</summary>
/// <param name="Name">Tool name as exposed to MCP clients.</param>
/// <param name="Title">Optional short title.</param>
/// <param name="Description">Tool description from a <see cref="DescriptionAttribute"/>.</param>
/// <param name="Parameters">The tool's parameters.</param>
public sealed record ToolDescriptor(
    string Name,
    string? Title,
    string? Description,
    IReadOnlyList<ToolParameterDescriptor> Parameters);

/// <summary>
/// Extracts the tool catalog (names, descriptions, parameter lists) from the tool classes via reflection.
/// This catalog is the single source of truth used both to build the global usage guide instructions
/// and to verify that every registered tool is documented.
/// </summary>
public static class ToolCatalog
{
    /// <summary>Scans the given types for methods marked with <see cref="McpServerToolAttribute"/>.</summary>
    public static IReadOnlyList<ToolDescriptor> FromTypes(IEnumerable<Type> toolTypes)
    {
        var descriptors = new List<ToolDescriptor>();

        foreach (Type toolType in toolTypes)
        {
            foreach (MethodInfo method in toolType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                string name = attribute.Name ?? method.Name;
                string? description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;

                var parameters = method
                    .GetParameters()
                    .Where(p => !string.Equals(p.Name, "cancellationToken", StringComparison.OrdinalIgnoreCase))
                    .Select(p => new ToolParameterDescriptor(
                        p.Name ?? "unnamed",
                        RenderTypeName(p.ParameterType),
                        IsRequired: !p.HasDefaultValue,
                        Description: p.GetCustomAttribute<DescriptionAttribute>()?.Description))
                    .ToList();

                descriptors.Add(new ToolDescriptor(name, attribute.Title, description, parameters));
            }
        }

        return descriptors
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Renders a CLR type as a human-readable JSON schema type name.</summary>
    public static string RenderTypeName(Type type)
    {
        Type effective = Nullable.GetUnderlyingType(type) ?? type;

        if (effective.IsArray)
        {
            return RenderTypeName(effective.GetElementType()!) + "[]";
        }

        return effective switch
        {
            _ when effective == typeof(string) => "string",
            _ when effective == typeof(bool) => "boolean",
            _ when effective == typeof(int) || effective == typeof(long) => "integer",
            _ when effective == typeof(double) || effective == typeof(decimal) => "number",
            _ when effective == typeof(DateTime) || effective == typeof(DateTimeOffset) => "string (date-time)",
            _ => effective.Name,
        };
    }
}
