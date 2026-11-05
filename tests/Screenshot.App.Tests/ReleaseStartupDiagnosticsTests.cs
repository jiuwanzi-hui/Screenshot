using System.Reflection;
using System.Reflection.Emit;
using Screenshot.App.Capture;
using Screenshot.App.Core;

namespace Screenshot.App.Tests;

public sealed class ReleaseStartupDiagnosticsTests
{
#if !DEBUG && !SNAPCUT_CAPTURE_DIAGNOSTICS
    [Fact]
    public void ProductionCodeHasNoStartupLoggingOrTimingMarkCalls()
    {
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => unchecked((ushort)code.Value));
        var assembly = typeof(App).Assembly;
        foreach (var type in assembly.GetTypes().Where(type => type != typeof(StartupDiagnostics) && type != typeof(CaptureTimingDiagnostics)))
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
            {
                if (method.GetMethodBody()?.GetILAsByteArray() is not { } il) continue;
                for (var index = 0; index < il.Length;)
                {
                    ushort value = il[index++];
                    if (value == 0xfe) value = (ushort)(0xfe00 | il[index++]);
                    var code = codes[value];
                    if (code.OperandType == OperandType.InlineMethod)
                    {
                        var target = method.Module.ResolveMethod(BitConverter.ToInt32(il, index), type.IsGenericType ? type.GetGenericArguments() : null,
                            method.IsGenericMethod ? method.GetGenericArguments() : null);
                        Assert.False(target?.DeclaringType == typeof(StartupDiagnostics), $"{type.Name}.{method.Name} still calls {target?.Name} in Release.");
                        Assert.False(target?.DeclaringType == typeof(CaptureTimingDiagnostics) && target.Name is "Mark" or "UiContext",
                            $"{type.Name}.{method.Name} still formats timing marks in Release.");
                    }
                    index += code.OperandType switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, index),
                        _ => 4,
                    };
                }
            }
        }
    }
#endif
}
