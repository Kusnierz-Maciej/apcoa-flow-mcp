using System.Reflection;
using ApcoaFlowApiClient.Api;
using ApcoaFlowMcp.Apcoa;

namespace ApcoaFlowMcp.Tests;

/// <summary>Rules about how the server's code is put together, checked on the compiled assembly.</summary>
public class ArchitectureTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                     BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Test]
    public async Task Parking_details_are_fetched_only_through_the_caching_wrapper()
    {
        // ParkingTime.GetParkingAsync remembers every parking's time zone; a direct GetSpaceAsync call would skip that.
        var target = typeof(IApcoaFlowApi).GetMethod(nameof(IApcoaFlowApi.GetSpaceAsync))!;
        var callers = CallersOf(target, typeof(ParkingTime).Assembly)
            .Where(t => Outermost(t) != typeof(ParkingTime))
            .Select(t => Outermost(t).FullName).Distinct().ToList();

        await Assert.That(callers).IsEmpty()
            .Because("call ParkingTime.GetParkingAsync (or Pricing.DetailsWithCurveAsync) instead of IApcoaFlowApi.GetSpaceAsync");
    }

    [Test]
    public async Task The_rule_check_sees_the_wrapper_itself()
    {
        // Guards the check above: it must find the one allowed call (inside an async state machine).
        var target = typeof(IApcoaFlowApi).GetMethod(nameof(IApcoaFlowApi.GetSpaceAsync))!;

        await Assert.That(CallersOf(target, typeof(ParkingTime).Assembly).Select(Outermost)).Contains(typeof(ParkingTime));
    }

    // The types (including compiler-generated ones: async state machines, lambdas) whose IL calls target.
    private static IEnumerable<Type> CallersOf(MethodInfo target, Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        foreach (var method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null) continue;
            for (var i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] is not (0x28 or 0x6F)) continue; // call, callvirt + a 4-byte method token
                MethodBase? called;
                try { called = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); }
                catch (ArgumentException) { continue; } // operand bytes that only look like an opcode
                if (called is MethodInfo m && m.DeclaringType == target.DeclaringType && m.Name == target.Name)
                {
                    yield return type;
                    break;
                }
            }
        }
    }

    private static Type Outermost(Type type)
    {
        while (type.DeclaringType is { } outer) type = outer;
        return type;
    }
}
