using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ReScene.SRR;

namespace ReScene.Tests;

/// <summary>
/// Locks the library's public API surface against an approved baseline so accidental additions
/// (or breaking removals) are caught in review rather than shipped. After a deliberate API change,
/// delete <c>PublicApi.ReScene.approved.txt</c> (or update it from the failure diff) and re-run to
/// regenerate the baseline, then commit it.
/// </summary>
public class PublicApiSnapshotTests
{
    [Fact]
    public void PublicApi_MatchesApprovedBaseline()
    {
        string actual = ComputePublicApi(typeof(SRRFile).Assembly);
        string baselinePath = GetBaselinePath();

        if (!File.Exists(baselinePath))
        {
            File.WriteAllText(baselinePath, actual);
            Assert.Fail($"Public API baseline did not exist; it was generated at '{baselinePath}'. Review and commit it, then re-run.");
        }

        string expected = File.ReadAllText(baselinePath).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(expected, actual);
    }

    private static string GetBaselinePath([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "PublicApi.ReScene.approved.txt");

    private static string ComputePublicApi(Assembly assembly)
    {
        var sb = new StringBuilder();
        foreach (Type type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            sb.Append(FormatType(type)).Append('\n');
            foreach (string member in GetApiMembers(type).OrderBy(m => m, StringComparer.Ordinal))
            {
                sb.Append("    ").Append(member).Append('\n');
            }
        }

        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string FormatType(Type type)
    {
        string kind = type.IsEnum ? "enum"
            : type.IsInterface ? "interface"
            : type.IsValueType ? "struct"
            : "class";

        var modifiers = new StringBuilder();
        if (!type.IsEnum && !type.IsInterface && !type.IsValueType)
        {
            if (type.IsAbstract && type.IsSealed)
            {
                modifiers.Append("static ");
            }
            else if (type.IsAbstract)
            {
                modifiers.Append("abstract ");
            }
            else if (type.IsSealed)
            {
                modifiers.Append("sealed ");
            }
        }

        return $"{kind} {modifiers}{type.FullName}";
    }

    private static IEnumerable<string> GetApiMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (MemberInfo member in type.GetMembers(flags))
        {
            switch (member)
            {
                case ConstructorInfo ctor when IsVisible(ctor):
                    yield return $"ctor ({FormatParameters(ctor.GetParameters())})";
                    break;

                case MethodInfo method when IsVisible(method) && !IsAccessor(method):
                    yield return $"method {TypeName(method.ReturnType)} {method.Name}({FormatParameters(method.GetParameters())})";
                    break;

                case PropertyInfo property when IsVisibleProperty(property):
                    yield return $"property {TypeName(property.PropertyType)} {property.Name} {{{Accessor("get", property.GetMethod)}{Accessor("set", property.SetMethod)} }}";
                    break;

                case FieldInfo field when IsVisible(field):
                    yield return $"field {TypeName(field.FieldType)} {field.Name}";
                    break;

                case EventInfo evt when evt.AddMethod is { } add && IsVisible(add):
                    yield return $"event {TypeName(evt.EventHandlerType!)} {evt.Name}";
                    break;
            }
        }
    }

    private static bool IsVisible(MethodBase m) => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly;

    private static bool IsVisible(FieldInfo f) => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly;

    private static bool IsVisibleProperty(PropertyInfo p)
        => (p.GetMethod is { } g && IsVisible(g)) || (p.SetMethod is { } s && IsVisible(s));

    private static bool IsAccessor(MethodInfo m)
        => m.IsSpecialName
            && (m.Name.StartsWith("get_", StringComparison.Ordinal)
                || m.Name.StartsWith("set_", StringComparison.Ordinal)
                || m.Name.StartsWith("add_", StringComparison.Ordinal)
                || m.Name.StartsWith("remove_", StringComparison.Ordinal));

    private static string Accessor(string name, MethodInfo? accessor)
        => accessor is not null && IsVisible(accessor) ? $" {name};" : string.Empty;

    private static string FormatParameters(ParameterInfo[] parameters)
        => string.Join(", ", parameters.Select(p => TypeName(p.ParameterType)));

    private static string TypeName(Type type)
    {
        if (type.IsArray)
        {
            return $"{TypeName(type.GetElementType()!)}[]";
        }

        if (type.IsByRef)
        {
            return $"{TypeName(type.GetElementType()!)}&";
        }

        if (type.IsGenericType)
        {
            string name = type.Name;
            int tick = name.IndexOf('`', StringComparison.Ordinal);
            if (tick >= 0)
            {
                name = name[..tick];
            }

            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
        }

        return type.Name;
    }
}
