# AxePower Architecture

* **.NET 8 WPF**: AxePower is built on .NET 8 for Windows Desktop.
* **C# 12**: Use modern C# features appropriately.
* **Strict MVVM**: 
  * `AxePower.App` contains Views (UI) and ViewModels.
  * `AxePower.Core` contains Models and Services (Business logic).
  * No business logic in Views. No WPF references in Core.
* **CommunityToolkit.Mvvm**: Use `[ObservableProperty]`, `[RelayCommand]`, and `[AsyncRelayCommand]`. Do not manually implement `INotifyPropertyChanged` unless necessary.
* **Dependency Injection**: Use `Microsoft.Extensions.DependencyInjection` in `App.xaml.cs`.
* **Constructor Injection**: Inject services into ViewModels and other services.
* **Interface-based Services**: Use interfaces for system-facing services where testability demands it.
* **Minimal Code-behind**: Keep `*.xaml.cs` limited to genuine window management and native platform integration.
* **Async Usage**: Use `Task` and `async/await`. Avoid `async void` except for genuine event handlers.
* **Cancellation and Disposal**: Always use `CancellationToken` for asynchronous tasks. Dispose of unmanaged resources, event subscriptions, and semaphores properly.
* **Reuse Abstractions**: Reuse existing services, models, and commands before creating new ones.
