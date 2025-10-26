using Moq;
using NetSdrClientApp;
using NetSdrClientApp.Networking;

namespace NetSdrClientAppTests;

public class NetSdrClientTests
{
    NetSdrClient _client;
    Mock<ITcpClient> _tcpMock;
    Mock<IUdpClient> _updMock;

    public NetSdrClientTests() { }

    [SetUp]
    public void Setup()
    {
        _tcpMock = new Mock<ITcpClient>();
        _tcpMock.Setup(tcp => tcp.Connect()).Callback(() =>
        {
            _tcpMock.Setup(tcp => tcp.Connected).Returns(true);
        });
        _tcpMock.Setup(tcp => tcp.Disconnect()).Callback(() =>
        {
            _tcpMock.Setup(tcp => tcp.Connected).Returns(false);
        });
        _tcpMock.Setup(tcp => tcp.SendMessageAsync(It.IsAny<byte[]>())).Callback<byte[]>((bytes) =>
        {
            _tcpMock.Raise(tcp => tcp.MessageReceived += null, _tcpMock.Object, bytes);
        });
        _updMock = new Mock<IUdpClient>();
        _client = new NetSdrClient(_tcpMock.Object, _updMock.Object);
    }

    [Test]
    public async Task ConnectAsyncTest()
    {
        //act
        await _client.ConnectAsync();
        //assert
        _tcpMock.Verify(tcp => tcp.Connect(), Times.Once);
        _tcpMock.Verify(tcp => tcp.SendMessageAsync(It.IsAny<byte[]>()), Times.Exactly(3));
    }

    [Test]
    public async Task DisconnectWithNoConnectionTest()
    {
        //act
        _client.Disconect();
        //assert
        //No exception thrown
        _tcpMock.Verify(tcp => tcp.Disconnect(), Times.Once);
    }

    [Test]
    public async Task DisconnectTest()
    {
        //Arrange 
        await ConnectAsyncTest();
        //act
        _client.Disconect();
        //assert
        //No exception thrown
        _tcpMock.Verify(tcp => tcp.Disconnect(), Times.Once);
    }

    [Test]
    public async Task StartIQNoConnectionTest()
    {
        //act
        await _client.StartIQAsync();
        //assert
        //No exception thrown
        _tcpMock.Verify(tcp => tcp.SendMessageAsync(It.IsAny<byte[]>()), Times.Never);
        _tcpMock.VerifyGet(tcp => tcp.Connected, Times.AtLeastOnce);
    }

    [Test]
    public async Task StartIQTest()
    {
        //Arrange 
        await ConnectAsyncTest();
        //act
        await _client.StartIQAsync();
        //assert
        //No exception thrown
        _updMock.Verify(udp => udp.StartListeningAsync(), Times.Once);
        Assert.That(_client.IQStarted, Is.True);
    }

    [Test]
    public async Task StopIQTest()
    {
        //Arrange 
        await ConnectAsyncTest();
        //act
        await _client.StopIQAsync();
        //assert
        //No exception thrown
        _updMock.Verify(tcp => tcp.StopListening(), Times.Once);
        Assert.That(_client.IQStarted, Is.False);
    }

    // ======== НОВІ 12 ТЕСТІВ ========

    [Test]
    public async Task ConnectAsync_WhenAlreadyConnected_ShouldNotConnectAgain()
    {
        // Arrange
        _tcpMock.Setup(tcp => tcp.Connected).Returns(true);

        // Act
        await _client.ConnectAsync();

        // Assert
        _tcpMock.Verify(tcp => tcp.Connect(), Times.Never);
        _tcpMock.Verify(tcp => tcp.SendMessageAsync(It.IsAny<byte[]>()), Times.Never);
    }

    [Test]
    public async Task ChangeFrequencyAsync_WithValidParameters_ShouldSendMessage()
    {
        // Arrange
        await ConnectAsyncTest();
        long frequency = 14250000; // 14.25 MHz
        int channel = 1;

        // Act
        await _client.ChangeFrequencyAsync(frequency, channel);

        // Assert
        _tcpMock.Verify(tcp => tcp.SendMessageAsync(It.IsAny<byte[]>()), Times.Exactly(4)); // 3 from connect + 1 from frequency change
    }

    [Test]
    public async Task ChangeFrequencyAsync_WithZeroFrequency_ShouldSendMessage()
    {
        // Arrange
        await ConnectAsyncTest();

        // Act
        await _client.ChangeFrequencyAsync(0, 0);

        // Assert
        _tcpMock.Verify(tcp => tcp.SendMessageAsync(It.IsAny<byte[]>()), Times.Exactly(4));
    }

    [Test]
    public async Task ChangeFrequencyAsync_WithMaxFrequency_ShouldSendMessage()
    {
        // Arrange
        await ConnectAsyncTest();
        long maxFrequency = long.MaxValue;

        // Act
        await _client.ChangeFrequencyAsync(maxFrequency, 255);

        // Assert
        _tcpMock.Verify(tcp => tcp.SendMessageAsync(It.IsAny<byte[]>()), Times.Exactly(4));
    }

    [Test]
    public async Task StopIQAsync_WithoutConnection_ShouldNotStopListening()
    {
        // Act
        await _client.StopIQAsync();

        // Assert
        _updMock.Verify(udp => udp.StopListening(), Times.Never);
        Assert.That(_client.IQStarted, Is.False);
    }

    [Test]
    public async Task StopIQAsync_AfterStarting_ShouldSetIQStartedToFalse()
    {
        // Arrange
        await ConnectAsyncTest();
        await _client.StartIQAsync();
        Assert.That(_client.IQStarted, Is.True);

        // Act
        await _client.StopIQAsync();

        // Assert
        Assert.That(_client.IQStarted, Is.False);
    }

    [Test]
    public async Task IQStarted_InitialValue_ShouldBeFalse()
    {
        // Assert
        Assert.That(_client.IQStarted, Is.False);
    }

    [Test]
    public async Task IQStarted_CanBeSetManually()
    {
        // Act
        _client.IQStarted = true;

        // Assert
        Assert.That(_client.IQStarted, Is.True);

        // Act
        _client.IQStarted = false;

        // Assert
        Assert.That(_client.IQStarted, Is.False);
    }

    [Test]
    public void TcpClient_MessageReceived_ShouldBeHandled()
    {
        // Arrange
        byte[] testMessage = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        // Act
        _tcpMock.Raise(tcp => tcp.MessageReceived += null, _tcpMock.Object, testMessage);

        // Assert
        // No exception thrown - message handled successfully
        Assert.Pass("TCP message received and handled without exceptions");
    }

    [Test]
    public void UdpClient_MessageReceived_ShouldBeHandled()
    {
        // Arrange
        byte[] testMessage = new byte[] { 
            0x00, 0x08, // Header
            0x00, 0x01, // Sequence
            0x01, 0x02, 0x03, 0x04 // Body
        };

        // Act & Assert
        Assert.DoesNotThrow(() =>
        {
            _updMock.Raise(udp => udp.MessageReceived += null, _updMock.Object, testMessage);
        });
    }

    [Test]
    public async Task MultipleConnectAsync_ShouldOnlyConnectOnce()
    {
        // Act
        await _client.ConnectAsync();
        await _client.ConnectAsync(); // Already connected
        await _client.ConnectAsync(); // Already connected

        // Assert
        _tcpMock.Verify(tcp => tcp.Connect(), Times.Once);
    }

    [Test]
    public async Task StartAndStopIQ_MultipleTimes_ShouldWorkCorrectly()
    {
        // Arrange
        await ConnectAsyncTest();

        // Act - Start/Stop цикл 3 рази
        await _client.StartIQAsync();
        await _client.StopIQAsync();
        
        await _client.StartIQAsync();
        await _client.StopIQAsync();
        
        await _client.StartIQAsync();
        await _client.StopIQAsync();

        // Assert
        _updMock.Verify(udp => udp.StartListeningAsync(), Times.Exactly(3));
        _updMock.Verify(udp => udp.StopListening(), Times.Exactly(3));
        Assert.That(_client.IQStarted, Is.False);
    }

    [Test]
    public async Task CompleteWorkflow_ConnectChangeFrequencyStartStopDisconnect()
    {
        // Arrange & Act - повний робочий цикл
        await _client.ConnectAsync();
        await _client.ChangeFrequencyAsync(7100000, 0);
        await _client.StartIQAsync();
        await _client.StopIQAsync();
        _client.Disconect();

        // Assert
        _tcpMock.Verify(tcp => tcp.Connect(), Times.Once);
        _tcpMock.Verify(tcp => tcp.Disconnect(), Times.Once);
        _tcpMock.Verify(tcp => tcp.SendMessageAsync(It.IsAny<byte[]>()), Times.AtLeast(5));
        _updMock.Verify(udp => udp.StartListeningAsync(), Times.Once);
        _updMock.Verify(udp => udp.StopListening(), Times.Once);
        Assert.That(_client.IQStarted, Is.False);
    }
}
