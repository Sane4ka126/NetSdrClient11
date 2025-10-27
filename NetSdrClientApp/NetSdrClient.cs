using NetSdrClientApp.Messages;
using NetSdrClientApp.Networking;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static NetSdrClientApp.Messages.NetSdrMessageHelper;

namespace NetSdrClientApp
{
    public class NetSdrClient : IDisposable
    {
        private readonly ITcpClient _tcpClient;
        private readonly IUdpClient _udpClient;
        private readonly string _sampleFilePath;
        private FileStream? _fileStream;
        private BinaryWriter? _binaryWriter;
        private readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);
        private bool _disposed = false;
        
        public bool IQStarted { get; set; }
        
        private TaskCompletionSource<byte[]>? _responseTaskSource;
        private readonly TimeSpan _responseTimeout = TimeSpan.FromSeconds(5);

        public NetSdrClient(ITcpClient tcpClient, IUdpClient udpClient, string sampleFilePath = "samples.bin")
        {
            _tcpClient = tcpClient ?? throw new ArgumentNullException(nameof(tcpClient));
            _udpClient = udpClient ?? throw new ArgumentNullException(nameof(udpClient));
            _sampleFilePath = sampleFilePath;

            _tcpClient.MessageReceived += TcpClientMessageReceived;
            _udpClient.MessageReceived += UdpClientMessageReceived;
        }

        public async Task ConnectAsync()
        {
            if (!_tcpClient.Connected)
            {
                _tcpClient.Connect();

                var sampleRate = BitConverter.GetBytes((long)100000).Take(5).ToArray();
                var automaticFilterMode = BitConverter.GetBytes((ushort)0).ToArray();
                var adMode = new byte[] { 0x00, 0x03 };

                var msgs = new List<byte[]>
                {
                    NetSdrMessageHelper.GetControlItemMessage(MsgTypes.SetControlItem, ControlItemCodes.IQOutputDataSampleRate, sampleRate),
                    NetSdrMessageHelper.GetControlItemMessage(MsgTypes.SetControlItem, ControlItemCodes.RFFilter, automaticFilterMode),
                    NetSdrMessageHelper.GetControlItemMessage(MsgTypes.SetControlItem, ControlItemCodes.ADModes, adMode),
                };

                foreach (var msg in msgs)
                {
                    await SendTcpRequestAsync(msg);
                }
            }
        }

        public void Disconnect()
        {
            _tcpClient.Disconnect();
            CloseFileStream();
        }

        public async Task StartIQAsync()
        {
            if (!_tcpClient.Connected)
            {
                Console.WriteLine("No active connection.");
                return;
            }

            var iqDataMode = (byte)0x80;
            var start = (byte)0x02;
            var fifo16bitCaptureMode = (byte)0x01;
            var n = (byte)1;

            var args = new[] { iqDataMode, start, fifo16bitCaptureMode, n };

            var msg = NetSdrMessageHelper.GetControlItemMessage(MsgTypes.SetControlItem, ControlItemCodes.ReceiverState, args);
            
            await SendTcpRequestAsync(msg);

            IQStarted = true;

            OpenFileStream();

            _ = _udpClient.StartListeningAsync();
        }

        public async Task StopIQAsync()
        {
            if (!_tcpClient.Connected)
            {
                Console.WriteLine("No active connection.");
                return;
            }

            var stop = (byte)0x01;

            var args = new byte[] { 0, stop, 0, 0 };

            var msg = NetSdrMessageHelper.GetControlItemMessage(MsgTypes.SetControlItem, ControlItemCodes.ReceiverState, args);

            await SendTcpRequestAsync(msg);

            IQStarted = false;

            _udpClient.StopListening();
            
            CloseFileStream();
        }

        public async Task ChangeFrequencyAsync(long hz, int channel)
        {
            var channelArg = (byte)channel;
            var frequencyArg = BitConverter.GetBytes(hz).Take(5);
            var args = new[] { channelArg }.Concat(frequencyArg).ToArray();

            var msg = NetSdrMessageHelper.GetControlItemMessage(MsgTypes.SetControlItem, ControlItemCodes.ReceiverFrequency, args);

            await SendTcpRequestAsync(msg);
        }

        private void OpenFileStream()
        {
            try
            {
                _fileStream = new FileStream(_sampleFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                _binaryWriter = new BinaryWriter(_fileStream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error opening file stream: {ex.Message}");
            }
        }

        private void CloseFileStream()
        {
            try
            {
                _binaryWriter?.Dispose();
                _binaryWriter = null;
                
                _fileStream?.Dispose();
                _fileStream = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error closing file stream: {ex.Message}");
            }
        }

        private async void UdpClientMessageReceived(object? sender, byte[] e)
        {
            try
            {
                NetSdrMessageHelper.TranslateMessage(e, out _, out _, out _, out byte[] body);
                var samples = NetSdrMessageHelper.GetSamples(16, body).ToArray();

                Console.WriteLine($"Samples received: {string.Join(" ", body.Select(b => Convert.ToString(b, toBase: 16)))}");

                await WriteSamplesToFileAsync(samples);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing UDP message: {ex.Message}");
            }
        }

        private async Task WriteSamplesToFileAsync(int[] samples)
        {
            if (_binaryWriter == null || _fileStream == null)
                return;

            await _fileLock.WaitAsync();
            try
            {
                foreach (var sample in samples)
                {
                    _binaryWriter.Write((short)sample);
                }
                await _fileStream.FlushAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing samples to file: {ex.Message}");
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private async Task<byte[]?> SendTcpRequestAsync(byte[] msg)
        {
            if (!_tcpClient.Connected)
            {
                Console.WriteLine("No active connection.");
                return null;
            }

            _responseTaskSource = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var responseTask = _responseTaskSource.Task;

            try
            {
                await _tcpClient.SendMessageAsync(msg);

                using (var cts = new CancellationTokenSource(_responseTimeout))
                {
                    var completedTask = await Task.WhenAny(responseTask, Task.Delay(_responseTimeout, cts.Token));
                    
                    if (completedTask == responseTask)
                    {
                        await cts.CancelAsync();
                        return await responseTask;
                    }
                    else
                    {
                        Console.WriteLine("Request timeout.");
                        _responseTaskSource?.TrySetCanceled();
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending TCP request: {ex.Message}");
                _responseTaskSource?.TrySetException(ex);
                return null;
            }
            finally
            {
                _responseTaskSource = null;
            }
        }

        private void TcpClientMessageReceived(object? sender, byte[] e)
        {
            try
            {
                if (_responseTaskSource != null)
                {
                    _responseTaskSource.TrySetResult(e);
                }
                Console.WriteLine($"Response received: {string.Join(" ", e.Select(b => Convert.ToString(b, toBase: 16)))}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing TCP message: {ex.Message}");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _tcpClient.MessageReceived -= TcpClientMessageReceived;
                    _udpClient.MessageReceived -= UdpClientMessageReceived;
                    
                    CloseFileStream();
                    _fileLock?.Dispose();
                    
                    if (_tcpClient is IDisposable tcpDisposable)
                        tcpDisposable.Dispose();
                        
                    if (_udpClient is IDisposable udpDisposable)
                        udpDisposable.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
