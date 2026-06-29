namespace Obsync.App.ViewModels;

/// <summary>A view model that loads its data asynchronously when navigated to.</summary>
public interface IAsyncViewModel
{
    Task LoadAsync();
}
