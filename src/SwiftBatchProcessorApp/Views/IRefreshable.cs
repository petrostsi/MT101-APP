namespace SwiftBatchApp.Views;

/// <summary>A view that re-reads its data when shown and after every background data refresh.</summary>
public interface IRefreshable
{
    void Refresh();
}
