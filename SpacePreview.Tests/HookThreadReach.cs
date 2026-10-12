using System.Reflection;
using System.Reflection.Emit;

namespace SpacePreview.Tests;

/// <summary>
/// What a hook thread can run, read from the compiled product and not from
/// a list someone keeps. Starting at the methods Windows or a delegate calls
/// on the thread, it follows every direct call into the product assembly.
/// A delegate call is where it stops, so each one is reported and the test
/// names its target as a root.
/// </summary>
internal static class HookThreadReach
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    public static readonly Assembly Product = typeof(SpacePreview.Services.KeyboardHook).Assembly;

    /// <summary>Every product method the roots can run, the roots included.</summary>
    public static IReadOnlyList<MethodBase> From(params MethodBase[] roots)
    {
        var seen = new HashSet<MethodBase>();
        var pending = new Stack<MethodBase>(roots);
        while (pending.Count > 0)
        {
            var method = pending.Pop();
            if (!seen.Add(method))
            {
                continue;
            }

            foreach (var callee in Calls(method))
            {
                if (Declared(callee).DeclaringType?.Assembly == Product)
                {
                    pending.Push(Declared(callee));
                }
            }
        }

        return [.. seen];
    }

    /// <summary>"Type.Method" for the methods a person wrote: no accessors, constructors or compiler output.</summary>
    public static IEnumerable<string> Names(IEnumerable<MethodBase> methods) =>
        methods
            .Where(method => !method.IsSpecialName && !method.IsConstructor && !method.Name.Contains('<'))
            .Select(Name)
            .Distinct()
            .Order(StringComparer.Ordinal);

    public static string Name(MethodBase method) => $"{TypeName(method.DeclaringType!)}.{method.Name}";

    /// <summary>Every method this one calls or constructs, in or out of the product.</summary>
    public static IEnumerable<MethodBase> Calls(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        var typeArguments = method.DeclaringType is { IsGenericType: true } type ? type.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var position = 0; position < il.Length;)
        {
            var value = il[position] == 0xFE
                ? unchecked((short)(0xFE00 | il[position + 1]))
                : il[position];
            var code = OpCodesByValue[value];
            position += code.Size;
            if (code.OperandType == OperandType.InlineMethod
                && (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj))
            {
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(il, position), typeArguments, methodArguments)!;
            }

            position += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
                _ => 4
            };
        }
    }

    /// <summary>"Caller -> delegate type" for each delegate the methods invoke.</summary>
    public static IEnumerable<string> DelegateCalls(IEnumerable<MethodBase> methods) =>
        methods
            .SelectMany(method => Calls(method)
                .Where(callee => callee.Name == "Invoke" && typeof(Delegate).IsAssignableFrom(callee.DeclaringType))
                .Select(callee => $"{Name(method)} -> {TypeName(callee.DeclaringType!)}"))
            .Distinct()
            .Order(StringComparer.Ordinal);

    /// <summary>The method as its type declares it, so one generic method is one entry.</summary>
    private static MethodBase Declared(MethodBase method) =>
        method is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } generic
            ? generic.GetGenericMethodDefinition()
            : method;

    private static string TypeName(Type type)
    {
        var name = type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>"
            : type.Name;
        return type.IsNested ? $"{TypeName(type.DeclaringType!)}.{name}" : name;
    }
}
