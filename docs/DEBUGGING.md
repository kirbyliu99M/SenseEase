# Phase 5 Runtime Debugging Notes

Date: 2026-05-17

## B1 App Startup Runtime Exceptions

### Exception #1 (before patch)

`dotnet run --project src/SenseEase.App` failed with:

```
System.InvalidOperationException: TwoWay 或 OneWayToSource 繫結無法在類型 'SenseEase.App.ViewModels.MainViewModel' 唯讀屬性 'Pressure' 上運作。
   at MS.Internal.Data.PropertyPathWorker.CheckReadOnly(Object item, Object info)
   at MS.Internal.Data.PropertyPathWorker.ReplaceItem(Int32 k, Object newO, Object parent)
   at MS.Internal.Data.PropertyPathWorker.UpdateSourceValueState(Int32 k, ICollectionView collectionView, Object newValue, Boolean isASubPropertyChange)
   at MS.Internal.Data.ClrBindingWorker.AttachDataItem()
   at System.Windows.Data.BindingExpression.Activate(Object item)
   at System.Windows.Data.BindingExpression.AttachToContext(AttachAttempt attempt)
   at System.Windows.Data.BindingExpression.MS.Internal.Data.IDataBindEngineClient.AttachToContext(Boolean lastChance)
   at MS.Internal.Data.DataBindEngine.Task.Run(Boolean lastChance)
   at MS.Internal.Data.DataBindEngine.Run(Object arg)
   at MS.Internal.Data.DataBindEngine.OnLayoutUpdated(Object sender, EventArgs e)
   at System.Windows.ContextLayoutManager.fireLayoutUpdateEvent()
   at System.Windows.ContextLayoutManager.UpdateLayout()
   at System.Windows.Interop.HwndSource.SetLayoutSize()
   at System.Windows.Interop.HwndSource.set_RootVisualInternal(Visual value)
   at System.Windows.Window.SetRootVisualAndUpdateSTC()
   at System.Windows.Window.SetupInitialState(Double requestedTop, Double requestedLeft, Double requestedWidth, Double requestedHeight)
   at System.Windows.Window.CreateSourceWindow(Boolean duringShow)
   at System.Windows.Window.ShowHelper(Object booleanBox)
   at System.Windows.Threading.ExceptionWrapper.InternalRealCall(Delegate callback, Object args, Int32 numArgs)
   at System.Windows.Threading.ExceptionWrapper.TryCatchWhen(Object source, Delegate callback, Object args, Int32 numArgs, Delegate catchHandler)
   at System.Windows.Threading.DispatcherOperation.InvokeImpl()
   at MS.Internal.CulturePreservingExecutionContext.CallbackWrapper(Object obj)
   at System.Threading.ExecutionContext.RunInternal(ExecutionContext executionContext, ContextCallback callback, Object state)
--- End of stack trace from previous location ---
   at System.Threading.ExecutionContext.RunInternal(ExecutionContext executionContext, ContextCallback callback, Object state)
   at MS.Internal.CulturePreservingExecutionContext.Run(CulturePreservingExecutionContext executionContext, ContextCallback callback, Object state)
   at System.Windows.Threading.DispatcherOperation.Invoke()
   at System.Windows.Threading.Dispatcher.ProcessQueue()
   at System.Windows.Threading.Dispatcher.WndProcHook(IntPtr hwnd, Int32 msg, IntPtr wParam, IntPtr lParam, Boolean& handled)
   at MS.Win32.HwndWrapper.WndProc(IntPtr hwnd, Int32 msg, IntPtr wParam, IntPtr lParam, Boolean& handled)
   at MS.Win32.HwndSubclass.DispatcherCallbackOperation(Object o)
   at System.Windows.Threading.ExceptionWrapper.InternalRealCall(Delegate callback, Object args, Int32 numArgs)
   at System.Windows.Threading.ExceptionWrapper.TryCatchWhen(Object source, Delegate callback, Object args, Int32 numArgs, Delegate catchHandler)
   at System.Windows.Threading.Dispatcher.LegacyInvokeImpl(DispatcherPriority priority, TimeSpan timeout, Delegate method, Object args, Int32 numArgs)
   at MS.Win32.HwndSubclass.SubclassWndProc(IntPtr hwnd, Int32 msg, IntPtr wParam, IntPtr lParam)
   at MS.Win32.UnsafeNativeMethods.DispatchMessage(MSG& msg)
   at System.Windows.Threading.Dispatcher.PushFrameImpl(DispatcherFrame frame)
   at System.Windows.Application.RunDispatcher(Object ignore)
   at System.Windows.Application.RunInternal(Window window)
   at SenseEase.App.App.Main()
```

### Exception #2 (after first patch, before second patch)

`dotnet run --project src/SenseEase.App` then failed with:

```
System.InvalidOperationException: TwoWay 或 OneWayToSource 繫結無法在類型 'SenseEase.App.ViewModels.MainViewModel' 唯讀屬性 'FovRadiusPercent' 上運作。
   at MS.Internal.Data.PropertyPathWorker.CheckReadOnly(Object item, Object info)
   at MS.Internal.Data.PropertyPathWorker.ReplaceItem(Int32 k, Object newO, Object parent)
   at MS.Internal.Data.PropertyPathWorker.UpdateSourceValueState(Int32 k, ICollectionView collectionView, Object newValue, Boolean isASubPropertyChange)
   at MS.Internal.Data.ClrBindingWorker.AttachDataItem()
   at System.Windows.Data.BindingExpression.Activate(Object item)
   at System.Windows.Data.BindingExpression.AttachToContext(AttachAttempt attempt)
   at System.Windows.Data.BindingExpression.MS.Internal.Data.IDataBindEngineClient.AttachToContext(Boolean lastChance)
   at MS.Internal.Data.DataBindEngine.Task.Run(Boolean lastChance)
   at MS.Internal.Data.DataBindEngine.Run(Object arg)
   at MS.Internal.Data.DataBindEngine.OnLayoutUpdated(Object sender, EventArgs e)
   at System.Windows.ContextLayoutManager.fireLayoutUpdateEvent()
   at System.Windows.ContextLayoutManager.UpdateLayout()
   at System.Windows.Interop.HwndSource.SetLayoutSize()
   at System.Windows.Interop.HwndSource.set_RootVisualInternal(Visual value)
   at System.Windows.Window.SetRootVisualAndUpdateSTC()
   at System.Windows.Window.SetupInitialState(Double requestedTop, Double requestedLeft, Double requestedWidth, Double requestedHeight)
   at System.Windows.Window.CreateSourceWindow(Boolean duringShow)
   at System.Windows.Window.ShowHelper(Object booleanBox)
   at System.Windows.Threading.ExceptionWrapper.InternalRealCall(Delegate callback, Object args, Int32 numArgs)
   at System.Windows.Threading.ExceptionWrapper.TryCatchWhen(Object source, Delegate callback, Object args, Int32 numArgs, Delegate catchHandler)
   at System.Windows.Threading.DispatcherOperation.InvokeImpl()
   at MS.Internal.CulturePreservingExecutionContext.CallbackWrapper(Object obj)
   at System.Threading.ExecutionContext.RunInternal(ExecutionContext executionContext, ContextCallback callback, Object state)
--- End of stack trace from previous location ---
   at System.Threading.ExecutionContext.RunInternal(ExecutionContext executionContext, ContextCallback callback, Object state)
   at MS.Internal.CulturePreservingExecutionContext.Run(CulturePreservingExecutionContext executionContext, ContextCallback callback, Object state)
   at System.Windows.Threading.DispatcherOperation.Invoke()
   at System.Windows.Threading.Dispatcher.ProcessQueue()
   at System.Windows.Threading.Dispatcher.WndProcHook(IntPtr hwnd, Int32 msg, IntPtr wParam, IntPtr lParam, Boolean& handled)
   at MS.Win32.HwndWrapper.WndProc(IntPtr hwnd, Int32 msg, IntPtr wParam, IntPtr lParam, Boolean& handled)
   at MS.Win32.HwndSubclass.DispatcherCallbackOperation(Object o)
   at System.Windows.Threading.ExceptionWrapper.InternalRealCall(Delegate callback, Object args, Int32 numArgs)
   at System.Windows.Threading.ExceptionWrapper.TryCatchWhen(Object source, Delegate callback, Object args, Int32 numArgs, Delegate catchHandler)
   at System.Windows.Threading.Dispatcher.LegacyInvokeImpl(DispatcherPriority priority, TimeSpan timeout, Delegate method, Object args, Int32 numArgs)
   at MS.Win32.HwndSubclass.SubclassWndProc(IntPtr hwnd, Int32 msg, IntPtr wParam, IntPtr lParam)
   at MS.Win32.UnsafeNativeMethods.DispatchMessage(MSG& msg)
   at System.Windows.Threading.Dispatcher.PushFrameImpl(DispatcherFrame frame)
   at System.Windows.Application.RunDispatcher(Object ignore)
   at System.Windows.Application.RunInternal(Window window)
   at SenseEase.App.App.Main()
```

### Startup status after patch

After setting `Mode=OneWay` on readonly `ProgressBar.Value` bindings, `dotnet run --project src/SenseEase.App` no longer throws startup exception (process remains running until command timeout in headless CLI session).

## B2/B4 Overlay lifecycle + ViewModel sync patch

Patched `MainWindow.xaml.cs`:

- Recreate overlay if `null` or `!IsLoaded` before `Show()`.
- Subscribe `OverlayWindow.Closed` and set `_viewModel.OverlayVisible = false`.
- Null out `_overlay` on close to avoid reusing a closed instance.

## B3 Overlay click-through runtime verification

`ClickThrough.Enable(this)` is already called in `OverlayWindow.SourceInitialized`, which is the recommended timing.

In this CLI/headless environment, interactive mouse hit-testing against underlying windows cannot be fully validated. Manual UI verification is still required on desktop session:

1. Toggle overlay on.
2. Confirm grid renders.
3. Click through to underlying app windows.

No code expansion was made beyond minimal lifecycle/sync patch.

## B5 Publish verification

Command:

`dotnet publish "src\SenseEase.App" -c Release -r win-x64 --self-contained -o publish_out`

Result: Success.

Artifacts:

- `publish_out\SenseEase.App.exe` exists (151,552 bytes).
- Publish folder total size: 167,539,164 bytes (~159.8 MiB).
