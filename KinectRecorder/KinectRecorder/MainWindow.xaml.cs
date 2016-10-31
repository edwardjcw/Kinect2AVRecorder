using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Accord.Math;
using Accord.Video.FFMPEG;
using AviFile;
using Microsoft.Kinect;
using NAudio;
using NAudio.Utils;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace KinectRecorder
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : INotifyPropertyChanged
    {

        private const int s_BytesPerSample = sizeof(float);
        private const int s_SamplesPerMillisecond = 16;

        private KinectSensor m_KinectSensor;

        private ColorFrameReader m_ColorFrameReader;
        private readonly WriteableBitmap m_ColorBitmap;
        private string m_StatusText;
        private VideoFileWriter m_Writer;

        private AudioBeamFrameReader m_AudioFrameReader;
        private AudioSource m_AudioSource;
        private WaveFileWriter m_WaveFileWriter;
        private int m_Offset;
        private WasapiCapture m_AudioCapture;
        private bool m_Recording;

        public event PropertyChangedEventHandler PropertyChanged;
        public ImageSource ImageSource => m_ColorBitmap;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            //get sensor
            m_KinectSensor = KinectSensor.GetDefault();
            m_KinectSensor.IsAvailableChanged += Sensor_IsAvailableChanged;
            m_KinectSensor.Open();

            //set up audio
            //m_AudioSource = m_KinectSensor.AudioSource;
            //m_AudioFrameReader = m_KinectSensor.AudioSource.OpenReader();
            //m_AudioFrameReader.FrameArrived += AudioFrameReaderOnFrameArrived;

            //set up video
            m_ColorFrameReader = m_KinectSensor.ColorFrameSource.OpenReader();
            m_ColorFrameReader.FrameArrived += Reader_ColorFrameArrived;
            FrameDescription colorFrameDescription =
                m_KinectSensor.ColorFrameSource.CreateFrameDescription(ColorImageFormat.Bgra);
            m_ColorBitmap = new WriteableBitmap(colorFrameDescription.Width, colorFrameDescription.Height, colorFrameDescription.LengthInPixels, colorFrameDescription.LengthInPixels, PixelFormats.Bgr32, null);
            
            //update status bar
            UpdateStatusText();

            var devices = new MMDeviceEnumerator();
            MMDeviceCollection endPoints = devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            MMDevice device = endPoints.FirstOrDefault(x => x.FriendlyName.Contains("Xbox NUI Sensor"));

            var waveFormat = new WaveFormat();
            
            if (device != null)
            {
                m_AudioCapture = new WasapiCapture(device);
                m_AudioCapture.DataAvailable += OnAudioCaptureOnDataAvailable;
                
                waveFormat = m_AudioCapture.WaveFormat;
            }

            m_Offset = 0;

            //var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
            
            m_WaveFileWriter = new WaveFileWriter("test.wav", waveFormat);

            //WaveStream stream = new RawSourceWaveStream();
            //WaveFileWriter.CreateWaveFile(tempFile, sourceStream);
            //set up writer
            m_Writer = new VideoFileWriter();
            m_Writer.Open("test.avi", colorFrameDescription.Width, colorFrameDescription.Height, 30, VideoCodec.H264);
            //40000, AudioCodec.None, 100000, 44000, 1); 

            m_Recording = false;
            m_AudioCapture?.StartRecording();
        }

        private void OnAudioCaptureOnDataAvailable(object sender, WaveInEventArgs args)
        {
            if (m_Recording)
            {
                m_WaveFileWriter.Write(args.Buffer, 0, args.BytesRecorded);
            }
            
        }

        private void AudioFrameReaderOnFrameArrived(object sender, AudioBeamFrameArrivedEventArgs audioBeamFrameArrivedEventArgs)
        {
            using (AudioBeamFrameList frameList = audioBeamFrameArrivedEventArgs.FrameReference.AcquireBeamFrames())
            {
                if (frameList == null)
                {
                    return;
                }

                var subFrameList = frameList[0].SubFrames;

                foreach (AudioBeamSubFrame subFrame in subFrameList)
                {
                    using (KinectBuffer sFrame = subFrame.LockAudioBuffer())
                    {
                        var audioBuffer = new byte[m_AudioSource.SubFrameLengthInBytes];

                        subFrame.CopyFrameDataToArray(audioBuffer);

                        m_WaveFileWriter.Write(audioBuffer, 0, audioBuffer.Length);
                        //m_Writer.WriteAudioFrame(audioBuffer);  
                        m_Offset ++;
                    }
                }
            }
        }

        private void Sensor_IsAvailableChanged(object sender, IsAvailableChangedEventArgs e)
        {
            UpdateStatusText();
        }

        private void UpdateStatusText()
        {
            if (m_KinectSensor != null)
            {
                StatusText = m_KinectSensor.IsAvailable 
                    ? Properties.Resources.RunningStatusText 
                    : Properties.Resources.SensorNotAvailableStatusText;
            }
            else StatusText = Properties.Resources.NoSensorStatusText;
        }

        public string StatusText
        {
            get { return m_StatusText; }
            set
            {
                if (m_StatusText == value) return;
                m_StatusText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("StatusText"));
            }
        }

        private void Reader_ColorFrameArrived(object sender, ColorFrameArrivedEventArgs e)
        {
            ThreadWriteVideo(e);
        }

        private void ThreadWriteVideo(object state)
        {
            using (ColorFrame colorFrame = ((ColorFrameArrivedEventArgs)state).FrameReference.AcquireFrame())
            {
                if (colorFrame == null)
                    return;

                FrameDescription colorFrameDescription = colorFrame.FrameDescription;

                using (KinectBuffer colorBuffer = colorFrame.LockRawImageBuffer())
                {
                    m_ColorBitmap.Lock();
                    if (colorFrameDescription.Width == m_ColorBitmap.PixelWidth && colorFrameDescription.Height == m_ColorBitmap.PixelHeight)
                    {
                        //ui stuff
                        var size = (uint)(colorFrameDescription.Width*colorFrameDescription.Height*4);
                        colorFrame.CopyConvertedFrameDataToIntPtr(m_ColorBitmap.BackBuffer, size, ColorImageFormat.Bgra);
                        m_ColorBitmap.AddDirtyRect(new Int32Rect(0,0, m_ColorBitmap.PixelWidth, m_ColorBitmap.PixelHeight));

                        BitmapSource source = BitmapSource.Create(m_ColorBitmap.PixelWidth, m_ColorBitmap.PixelHeight, m_ColorBitmap.DpiX, m_ColorBitmap.DpiY, m_ColorBitmap.Format, m_ColorBitmap.Palette, m_ColorBitmap.BackBuffer, (int) size, m_ColorBitmap.BackBufferStride);

                        using (var stream = new MemoryStream())
                        {
                            source.Freeze();
                            BitmapEncoder bitmapEncoder = new BmpBitmapEncoder();
                            bitmapEncoder.Frames.Add(BitmapFrame.Create(source));

                            bitmapEncoder.Save(stream);
                            var bitmap = new Bitmap(stream);

                            m_Writer.WriteVideoFrame(bitmap);
                            m_Recording = true;

                        }
                    }
                    m_ColorBitmap.Unlock();
                }

            }            
        }

        private void WriteVideo(BitmapSource source)
        {

        }


        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            m_AudioCapture?.StopRecording();
            m_AudioCapture?.Dispose();
            m_WaveFileWriter.Close();
            DisposeAudioFrameReader();
            DisposeColorFrameReader();
            CloseKinectSensor();
            CloseVideoRecorder();



            CombineAndSaveAv();
        }

        private static void CombineAndSaveAv()
        {
            var manager = new AviManager("test.avi", true);
            manager.AddAudioStream("test.wav", 0);
            manager.Close();
        }

        private void DisposeAudioFrameReader()
        {
            if (m_AudioFrameReader == null) return;
            m_AudioFrameReader.Dispose();
            m_AudioFrameReader = null;
        }


        private void DisposeColorFrameReader()
        {
            if (m_ColorFrameReader == null) return;
            m_Recording = false;
            m_ColorFrameReader.Dispose();
            m_ColorFrameReader = null;
        }

        private void CloseKinectSensor()
        {
            if (m_KinectSensor == null) return;
            m_KinectSensor.Close();
            m_KinectSensor = null;
        }

        private void CloseVideoRecorder()
        {
            if (m_Writer == null) return;
            m_Writer.Close();
            m_Writer = null;
        }
        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {

        }
    }
}
