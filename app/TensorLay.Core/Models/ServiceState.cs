namespace TensorLay.Models;

public enum ServiceState { NotInstalled, Installing, Stopped, Starting, Running, Stopping, Error }

public enum DownloadState { Pending, Downloading, Completed, Failed, Cancelled }
