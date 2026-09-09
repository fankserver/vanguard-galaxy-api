namespace VGModAPI.Core;

/// <summary>Internal reentrancy fence for engines that can be invoked while publishing native observations.</summary>
internal interface ICallbackDispatch
{
    bool IsDispatchingCallbacks { get; }
}
