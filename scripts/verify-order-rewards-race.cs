// Run against the compiled web assembly. Deferred JS results exercise real component state.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.JSInterop;

var folder = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(folder, name.Name + ".dll"))
    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, name.Name + ".dll")) : null;
var web = Assembly.LoadFrom(Path.Combine(folder, "TideCasa.Blazor.dll"));
var contracts = Assembly.LoadFrom(Path.Combine(folder, "TideCasa.Contracts.dll"));
var type = web.GetType("TideCasa.Blazor.Components.Pages.RestaurantOrder", true)!;
const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
void Field(object component, string name, object? value) => type.GetField(name, flags)!.SetValue(component, value);
object Record(string name, string key, string value)
{
    var recordType = contracts.GetType("TideCasa.Contracts." + name, true)!;
    var record = RuntimeHelpers.GetUninitializedObject(recordType);
    recordType.GetProperty(key)!.SetValue(record, value);
    return record;
}
(object Component, DelayedJs Js) Setup()
{
    var component = Activator.CreateInstance(type)!;
    var js = new DelayedJs();
    type.GetProperty("JS", flags)!.SetValue(component, js);
    type.GetProperty("Slug")!.SetValue(component, "bistro");
    Field(component, "menuRevision", 1);
    Field(component, "receipt", Record("RestaurantOrderReceipt", "OrderId", "order-one"));
    Field(component, "pending", Record("RestaurantOrderRequest", "TrackingKey", "key-one"));
    return (component, js);
}
Task Start(object component) => (Task)type.GetMethod("LinkRewardsAsync", flags)!.Invoke(component, null)!;
bool Linked(object component) => (bool)type.GetField("rewardsLinked", flags)!.GetValue(component)!;
void Check(string name, bool pass)
{
    if (!pass) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
}
foreach (var scenario in new[] { "menu", "slug", "receipt", "key" })
{
    var (component, js) = Setup();
    var task = Start(component);
    Check(scenario + ": linking really waits for browser", !task.IsCompleted && js.Pending.Count == 1);
    switch (scenario)
    {
        case "menu": Field(component, "menuRevision", 2); break;
        case "slug": type.GetProperty("Slug")!.SetValue(component, "another-restaurant"); break;
        case "receipt": Field(component, "receipt", Record("RestaurantOrderReceipt", "OrderId", "order-two")); break;
        case "key": Field(component, "pending", Record("RestaurantOrderRequest", "TrackingKey", "key-two")); break;
    }
    js.Pending[0].SetResult(true); await task;
    Check(scenario + ": old result cannot label the new order connected", !Linked(component));
    var current = Start(component); js.Pending[1].SetResult(true); await current;
    Check(scenario + ": current order still links normally", Linked(component));
}
foreach (var failure in new[] { false, true })
{
    var (component, js) = Setup();
    var earlier = Start(component); var latest = Start(component);
    js.Pending[1].SetResult(true); await latest;
    if (failure) js.Pending[0].SetException(new JSException("Synthetic failed request"));
    else js.Pending[0].SetResult(false);
    await earlier;
    Check("Successful receipt connection survives a later " + (failure ? "error" : "false result"), Linked(component));
}

sealed class DelayedJs : IJSRuntime
{
    public List<TaskCompletionSource<bool>> Pending { get; } = [];
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        if (identifier != "tideOrderRewards.link" || typeof(TValue) != typeof(bool)) throw new InvalidOperationException("Unexpected browser request");
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending.Add(pending);
        return new ValueTask<TValue>((Task<TValue>)(object)pending.Task);
    }
}
