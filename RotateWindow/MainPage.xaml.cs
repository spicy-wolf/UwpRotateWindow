using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics;
using Windows.Storage;
using Windows.UI.Composition;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
// win2d
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using System.Reflection;

namespace RotateWindow
{
    public sealed partial class MainPage : Page
    {
        // Configs
        private int rotate = 0;
        private bool flipX = false;
        private bool flipY = false;
        private Matrix3x2 frameTransformMatrix = Matrix3x2.Identity;

        // Capture API objects.
        private SizeInt32 _lastSize;
        private GraphicsCaptureItem _item;
        private Direct3D11CaptureFramePool _framePool;
        private GraphicsCaptureSession _session;

        // Non-API related members.
        private CanvasDevice _canvasDevice;
        private CompositionGraphicsDevice _compositionGraphicsDevice;
        private Compositor _compositor;
        private CompositionDrawingSurface _surface;

        public MainPage()
        {
            this.InitializeComponent();
            Setup();
        }

        private void Setup()
        {
            _canvasDevice = new CanvasDevice();

            _compositionGraphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(
                Window.Current.Compositor,
                _canvasDevice);

            _compositor = Window.Current.Compositor;

            _lastSize = new SizeInt32
            {
                Width = 0,
                Height = 0
            };

            _surface = _compositionGraphicsDevice.CreateDrawingSurface(
                new Size(_lastSize.Width, _lastSize.Height),
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                DirectXAlphaMode.Premultiplied);    // This is the only value that currently works with
                                                    // the composition APIs.

            var visual = _compositor.CreateSpriteVisual();
            visual.RelativeSizeAdjustment = Vector2.One;
            var brush = _compositor.CreateSurfaceBrush(_surface);
            brush.HorizontalAlignmentRatio = 0.5f;
            brush.VerticalAlignmentRatio = 0.5f;
            brush.Stretch = CompositionStretch.Uniform;
            visual.Brush = brush;
            ElementCompositionPreview.SetElementChildVisual(MainPanel, visual);
        }

        public async Task StartCaptureAsync()
        {
            // The GraphicsCapturePicker follows the same pattern the
            // file pickers do.
            var picker = new GraphicsCapturePicker();
            GraphicsCaptureItem item = await picker.PickSingleItemAsync();

            // The item may be null if the user dismissed the
            // control without making a selection or hit Cancel.
            if (item != null)
            {
                StartCaptureInternal(item);
            }
        }

        private void StartCaptureInternal(GraphicsCaptureItem item)
        {
            // Stop the previous capture if we had one.
            StopCapture();

            _item = item;
            _lastSize = _item.Size;
            UpdateFrameMatrix(new Size(_lastSize.Width, _lastSize.Height), rotate, flipX, flipY);

            _framePool = Direct3D11CaptureFramePool.Create(
               _canvasDevice, // D3D device
               DirectXPixelFormat.B8G8R8A8UIntNormalized, // Pixel format
               2, // Number of frames
               _item.Size); // Size of the buffers

            _framePool.FrameArrived += (s, a) =>
            {
                // The FrameArrived event is raised for every frame on the thread
                // that created the Direct3D11CaptureFramePool. This means we
                // don't have to do a null-check here, as we know we're the only
                // one dequeueing frames in our application.  

                // NOTE: Disposing the frame retires it and returns  
                // the buffer to the pool.

                using (var frame = _framePool.TryGetNextFrame())
                {
                    ProcessFrame(frame);
                }
            };

            _item.Closed += (s, a) =>
            {
                StopCapture();
            };

            _session = _framePool.CreateCaptureSession(_item);
            _session.StartCapture();
        }

        public void StopCapture()
        {
            _session?.Dispose();
            _framePool?.Dispose();
            _item = null;
            _session = null;
            _framePool = null;
        }

        private void ProcessFrame(Direct3D11CaptureFrame frame)
        {
            // Resize and device-lost leverage the same function on the
            // Direct3D11CaptureFramePool. Refactoring it this way avoids
            // throwing in the catch block below (device creation could always
            // fail) along with ensuring that resize completes successfully and
            // isn’t vulnerable to device-lost.
            bool needsReset = false;
            bool recreateDevice = false;

            if ((frame.ContentSize.Width != _lastSize.Width) ||
                (frame.ContentSize.Height != _lastSize.Height))
            {
                needsReset = true;
                _lastSize = frame.ContentSize;
                UpdateFrameMatrix(new Size(_lastSize.Width, _lastSize.Height), rotate, flipX, flipY);
            }

            try
            {
                // Take the D3D11 surface and draw it into a  
                // Composition surface.

                // Convert our D3D11 surface into a Win2D object.
                CanvasBitmap canvasBitmap = CanvasBitmap.CreateFromDirect3D11Surface(
                    _canvasDevice,
                    frame.Surface);

                // Helper that handles the drawing for us.
                FillSurfaceWithBitmap(canvasBitmap);
            }

            // This is the device-lost convention for Win2D.
            catch (Exception e) when (_canvasDevice.IsDeviceLost(e.HResult))
            {
                // We lost our graphics device. Recreate it and reset
                // our Direct3D11CaptureFramePool.  
                needsReset = true;
                recreateDevice = true;
            }

            if (needsReset)
            {
                ResetFramePool(frame.ContentSize, recreateDevice);
            }
        }

        private void FillSurfaceWithBitmap(CanvasBitmap canvasBitmap)
        {
            // Rotate result canvas
            CanvasComposition.Resize(_surface, this.rotate % 180 == 0 ? canvasBitmap.Size : new Size(canvasBitmap.Size.Height, canvasBitmap.Size.Width));
            //CanvasComposition.Resize(_surface, canvasBitmap.Size);

            using (var session = CanvasComposition.CreateDrawingSession(_surface))
            {
                session.Clear(Colors.Transparent);
                session.Transform = frameTransformMatrix;
                session.DrawImage(canvasBitmap);
            }
        }

        private void ResetFramePool(SizeInt32 size, bool recreateDevice)
        {
            do
            {
                try
                {
                    if (recreateDevice)
                    {
                        _canvasDevice = new CanvasDevice();
                    }

                    _framePool.Recreate(
                        _canvasDevice,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        2,
                        size);
                }
                // This is the device-lost convention for Win2D.
                catch (Exception e) when (_canvasDevice.IsDeviceLost(e.HResult))
                {
                    _canvasDevice = null;
                    recreateDevice = true;
                }
            } while (_canvasDevice == null);
        }

        private async void Button_ClickAsync(object sender, RoutedEventArgs e)
        {
            await StartCaptureAsync();
        }

        private void RotateDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            this.rotate = Int32.Parse((e.AddedItems[0] as ComboBoxItem).Content as string);

            // recompute draw matrix
            UpdateFrameMatrix(new Size(_lastSize.Width, _lastSize.Height), rotate, flipX, flipY);
        }

        private void FlipX_Clicked(object sender, RoutedEventArgs e)
        {
            flipX = (sender as CheckBox).IsChecked.GetValueOrDefault(false);
            // recompute draw matrix
            UpdateFrameMatrix(new Size(_lastSize.Width, _lastSize.Height), rotate, flipX, flipY);
        }
        private void FlipY_Clicked(object sender, RoutedEventArgs e)
        {
            flipY = (sender as CheckBox).IsChecked.GetValueOrDefault(false);
            // recompute draw matrix
            UpdateFrameMatrix(new Size(_lastSize.Width, _lastSize.Height), rotate, flipX, flipY);
        }

        private void UpdateFrameMatrix(Size captureSize, double rotateDegree, bool flipX, bool flipY)
        {
            // Set up a transformation matrix to rotate around the center of the canvas by 90 degrees
            frameTransformMatrix = Matrix3x2.Identity;
            // move center point to 0,0
            var moveToCenter = Matrix3x2.CreateTranslation((float)-captureSize.Width / 2, (float)-captureSize.Height / 2);
            frameTransformMatrix *= moveToCenter;
            // rotate
            var angleInRadians = (float)(rotateDegree * Math.PI / 180);
            var rotateMatrix = Matrix3x2.CreateRotation(angleInRadians);
            frameTransformMatrix *= rotateMatrix;

            // check flip
            if (flipX)
            {
                var flipXMatrix = Matrix3x2.CreateScale(-1, 1);
                frameTransformMatrix *= flipXMatrix;
            }
            if (flipY)
            {
                var flipXMatrix = Matrix3x2.CreateScale(1, -1);
                frameTransformMatrix *= flipXMatrix;
            }

            // move center point back
            var moveBack = Matrix3x2.CreateTranslation(
                (float)(Math.Abs((Math.Sin(angleInRadians) * captureSize.Height) + (Math.Cos(angleInRadians) * captureSize.Width)) / 2),
                (float)(Math.Abs((Math.Cos(angleInRadians) * captureSize.Height) + (Math.Sin(angleInRadians) * captureSize.Width)) / 2)
            );
            frameTransformMatrix *= moveBack;
        }
    }
}
