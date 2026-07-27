public class DownloadBackgroundWorker
{
    public ulong TotalSize;
    private ulong _totalDownloadedSize = 0;
    public event ProgressChangedEventHandler? ProgressChanged;

    public DownloadBackgroundWorker(ulong totalSize = 0)
    {
        TotalSize = totalSize;
    }

    public void ReportProgressPercentage(int progressPercentage)
    {
        ProgressChanged?.Invoke(progressPercentage);
    }

    /// <remarks>
    /// A worker with no TotalSize set is reporting on something whose size nobody declared, which is a
    /// legitimate thing for a caller to want -- it just has no percentage to give. That used to divide
    /// by zero and take the whole transfer down with it, which is a lot of damage for a progress bar to
    /// do; the transfer itself never needed the number.
    /// </remarks>
    public void ReportProgressAmount(ulong amountDownloaded)
    {
        _totalDownloadedSize += amountDownloaded;
        if (TotalSize == 0)
            return;
        ReportProgressPercentage((int)(100 * _totalDownloadedSize / TotalSize));
    }

}
