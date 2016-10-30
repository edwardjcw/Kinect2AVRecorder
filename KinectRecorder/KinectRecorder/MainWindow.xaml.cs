using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Accord.Video.FFMPEG;
using Microsoft.Kinect;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Accord;
using Accord.IO;

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
        private bool m_Recording;
        private VideoFileWriter m_Writer;


        private Stream m_AudioStream;
        private AudioBeamFrameReader m_AudioFrameReader;
        private AudioSource m_AudioSource;
        private byte[] m_AudioBuffer;
        private float m_BeamAngle;
        private float m_BeamAngleConfidence;
        

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
            m_AudioSource = m_KinectSensor.AudioSource;
            m_AudioBuffer = new byte[m_AudioSource.SubFrameLengthInBytes];
            m_AudioFrameReader = m_KinectSensor.AudioSource.OpenReader();
            m_AudioFrameReader.FrameArrived += AudioFrameReaderOnFrameArrived;

            //set up video
            m_ColorFrameReader = m_KinectSensor.ColorFrameSource.OpenReader();
            m_ColorFrameReader.FrameArrived += Reader_ColorFrameArrived;
            FrameDescription colorFrameDescription =
                m_KinectSensor.ColorFrameSource.CreateFrameDescription(ColorImageFormat.Bgra);
            m_ColorBitmap = new WriteableBitmap(colorFrameDescription.Width, colorFrameDescription.Height, 96.0, 96.0, PixelFormats.Bgr32, null);
            
            //update status bar
            UpdateStatusText();

            //set up writer
            m_Writer = new VideoFileWriter();
            m_Writer.Open("test.avi", colorFrameDescription.Width, colorFrameDescription.Height, 30, VideoCodec.Default,
                400000, AudioCodec.None, 99000, 44000, 1); 


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
                    subFrame.CopyFrameDataToArray(m_AudioBuffer);
                    m_Writer.WriteAudioFrame(m_AudioBuffer);

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
            DisposeAudioFrameReader();
            DisposeColorFrameReader();
            CloseKinectSensor();
            CloseVideoRecorder();
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
