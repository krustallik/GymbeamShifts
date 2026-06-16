using System.Reflection;

namespace GymBeamShiftsControllerX.Tests;

internal static class ReflectionTestHelper
{
    public static MethodInfo GetStaticMethod(Type type, string name)
    {
        return type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
               ?? throw new InvalidOperationException($"Static method {type.Name}.{name} not found.");
    }

    public static MethodInfo GetInstanceMethod(Type type, string name)
    {
        return type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
               ?? throw new InvalidOperationException($"Instance method {type.Name}.{name} not found.");
    }

    public static void SetStaticField(Type type, string name, object? value)
    {
        var field = type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"Static field {type.Name}.{name} not found.");
        field.SetValue(null, value);
    }

    public static object? GetStaticField(Type type, string name)
    {
        var field = type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"Static field {type.Name}.{name} not found.");
        return field.GetValue(null);
    }
}
