﻿using System;
using System.Windows.Forms;
using System.Linq;
using NTwain.Data;
using NTwain.Values;
using NTwain.Triplets;
using NTwain;
using System.Diagnostics;
using System.IO;
using System.Drawing.Imaging;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Reflection;
using CommonWin32;
using System.Threading;

namespace Tester.Winform
{
    sealed partial class TestForm : Form
    {
        ImageCodecInfo _tiffCodecInfo;
        TwainSessionWinform _twain;
        bool _stopScan;
        bool _loadingCaps;


        #region setup & cleanup

        public TestForm()
        {
            InitializeComponent();
            if (IntPtr.Size == 8)
            {
                Text = Text + " (64bit)";
            }
            else
            {
                Text = Text + " (32bit)";
            }
            foreach (var enc in ImageCodecInfo.GetImageEncoders())
            {
                if (enc.MimeType == "image/tiff") { _tiffCodecInfo = enc; break; }
            }
            SetupTwain();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && _twain.State > 4)
            {
                e.Cancel = true;
            }
            else
            {
                CleanupTwain();
            }
            base.OnFormClosing(e);
        }

        private void SetupTwain()
        {
            var appId = TWIdentity.CreateFromAssembly(DataGroups.Image, Assembly.GetEntryAssembly());
            _twain = new TwainSessionWinform(appId);
            _twain.DataTransferred += (s, e) =>
            {
                if (pictureBox1.Image != null)
                {
                    pictureBox1.Image.Dispose();
                    pictureBox1.Image = null;
                }
                if (e.Data != IntPtr.Zero)
                {
                    //_ptrTest = e.Data;
                    var img = e.Data.GetDrawingBitmap();
                    if (img != null)
                        pictureBox1.Image = img;
                }
                else if (!string.IsNullOrEmpty(e.FilePath))
                {
                    var img = new Bitmap(e.FilePath);
                    pictureBox1.Image = img;
                }
            };
            _twain.SourceDisabled += (s, e) =>
            {
                btnStopScan.Enabled = false;
                btnStartCapture.Enabled = true;
                panelOptions.Enabled = true;
                LoadSourceCaps();
            };
            _twain.TransferReady += (s, e) =>
            {
                e.CancelAll = _stopScan;
            };
            Application.AddMessageFilter(_twain);
        }

        private void CleanupTwain()
        {
            Application.RemoveMessageFilter(_twain);
            if (_twain.State == 4)
            {
                _twain.CloseSource();
            }
            if (_twain.State == 3)
            {
                _twain.CloseManager();
            }

            if (_twain.State > 2)
            {
                // normal close down didn't work, do hard kill
                _twain.ForceStepDown(2);
            }
        }

        #endregion

        #region toolbar

        private void btnSources_DropDownOpening(object sender, EventArgs e)
        {
            if (btnSources.DropDownItems.Count == 2)
            {
                ReloadSourceList();
            }
        }

        private void reloadSourcesListToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ReloadSourceList();
        }

        void SourceMenuItem_Click(object sender, EventArgs e)
        {
            // do nothing if source is enabled
            if (_twain.State > 4) { return; }

            if (_twain.State == 4) { _twain.CloseSource(); }

            foreach (var btn in btnSources.DropDownItems)
            {
                var srcBtn = btn as ToolStripMenuItem;
                if (srcBtn != null) { srcBtn.Checked = false; }
            }

            var curBtn = (sender as ToolStripMenuItem);
            var src = curBtn.Tag as TWIdentity;
            if (_twain.OpenSource(src.ProductName) == ReturnCode.Success)
            {
                curBtn.Checked = true;
                btnStartCapture.Enabled = true;
                LoadSourceCaps();
            }
        }

        private void btnStartCapture_Click(object sender, EventArgs e)
        {
            if (_twain.State == 4)
            {
                var hand = new HandleRef(this, this.Handle);
                _stopScan = false;

                if (_twain.SupportedCaps.Contains(CapabilityId.CapUIControllable))
                {
                    // hide scanner ui if possible
                    if (_twain.EnableSource(SourceEnableMode.NoUI, false, hand, SynchronizationContext.Current) == ReturnCode.Success)
                    {
                        btnStopScan.Enabled = true;
                        btnStartCapture.Enabled = false;
                        panelOptions.Enabled = false;
                    }
                }
                else
                {
                    if (_twain.EnableSource(SourceEnableMode.ShowUI, false, hand, SynchronizationContext.Current) == ReturnCode.Success)
                    {
                        btnStopScan.Enabled = true;
                        btnStartCapture.Enabled = false;
                        panelOptions.Enabled = false;
                    }
                }
            }
        }

        private void btnStopScan_Click(object sender, EventArgs e)
        {
            _stopScan = true;
        }

        private void btnSaveImage_Click(object sender, EventArgs e)
        {
            var img = pictureBox1.Image;

            if (img != null)
            {
                switch (img.PixelFormat)
                {
                    case PixelFormat.Format1bppIndexed:
                        saveFileDialog1.Filter = "tiff files|*.tif";
                        break;
                    default:
                        saveFileDialog1.Filter = "png files|*.png";
                        break;
                }

                if (saveFileDialog1.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    if (saveFileDialog1.FileName.EndsWith(".tif", StringComparison.OrdinalIgnoreCase))
                    {
                        EncoderParameters tiffParam = new EncoderParameters(1);

                        tiffParam.Param[0] = new EncoderParameter(Encoder.Compression, (long)EncoderValue.CompressionCCITT4);

                        pictureBox1.Image.Save(saveFileDialog1.FileName, _tiffCodecInfo, tiffParam);
                    }
                    else
                    {
                        pictureBox1.Image.Save(saveFileDialog1.FileName, ImageFormat.Png);
                    }
                }
            }
        }
        #endregion

        #region real work

        private void ReloadSourceList()
        {
            if (_twain.State < 3)
            {
                _twain.OpenManager(new HandleRef(this, this.Handle));
            }

            if (_twain.State >= 3)
            {
                while (btnSources.DropDownItems.IndexOf(sepSourceList) > 0)
                {
                    var first = btnSources.DropDownItems[0];
                    first.Click -= SourceMenuItem_Click;
                    btnSources.DropDownItems.Remove(first);
                }
                foreach (var src in _twain.GetSources())
                {
                    var srcBtn = new ToolStripMenuItem(src.ProductName);
                    srcBtn.Tag = src;
                    srcBtn.Click += SourceMenuItem_Click;
                    srcBtn.Checked = _twain.SourceId != null && _twain.SourceId.Id == src.Id;
                    btnSources.DropDownItems.Insert(0, srcBtn);
                }
            }
        }


        #region cap control


        private void LoadSourceCaps()
        {
            var caps = _twain.SupportedCaps;
            _loadingCaps = true;
            if (groupDepth.Enabled = caps.Contains(CapabilityId.ICapPixelType))
            {
                LoadDepth();
            }
            if (groupDPI.Enabled = caps.Contains(CapabilityId.ICapXResolution) && caps.Contains(CapabilityId.ICapYResolution))
            {
                LoadDPI();
            }
            // TODO: find out if this is how duplex works or also needs the other option
            if (groupDuplex.Enabled = caps.Contains(CapabilityId.CapDuplexEnabled))
            {
                LoadDuplex();
            }
            if (groupSize.Enabled = caps.Contains(CapabilityId.ICapSupportedSizes))
            {
                LoadPaperSize();
            }
            btnAllSettings.Enabled = caps.Contains(CapabilityId.CapEnableDSUIOnly);
            _loadingCaps = false;
        }

        private void LoadPaperSize()
        {
            var list = _twain.CapGetSupportedSizes();
            comboSize.DataSource = list;
            var cur = _twain.GetCurrentCap<SupportedSize>(CapabilityId.ICapSupportedSizes);
            if (list.Contains(cur))
            {
                comboSize.SelectedItem = cur;
            }
        }

        private void LoadDuplex()
        {
            ckDuplex.Checked = _twain.GetCurrentCap<uint>(CapabilityId.CapDuplexEnabled) != 0;
        }

        private void LoadDPI()
        {
            // only allow dpi of certain values for those source that lists everything
            var list = _twain.CapGetDPIs().Where(dpi => (dpi % 50) == 0).ToList();
            comboDPI.DataSource = list;
            var cur = _twain.GetCurrentCap<int>(CapabilityId.ICapXResolution);
            if (list.Contains(cur))
            {
                comboDPI.SelectedItem = cur;
            }
        }

        private void LoadDepth()
        {
            var list = _twain.CapGetPixelTypes();
            comboDepth.DataSource = list;
            var cur = _twain.GetCurrentCap<PixelType>(CapabilityId.ICapPixelType);
            if (list.Contains(cur))
            {
                comboDepth.SelectedItem = cur;
            }
        }

        private void comboSize_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!_loadingCaps && _twain.State == 4)
            {
                var sel = (SupportedSize)comboSize.SelectedItem;
                _twain.CapSetSupportedSize(sel);
            }
        }

        private void comboDepth_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!_loadingCaps && _twain.State == 4)
            {
                var sel = (PixelType)comboDepth.SelectedItem;
                _twain.CapSetPixelType(sel);
            }
        }

        private void comboDPI_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!_loadingCaps && _twain.State == 4)
            {
                var sel = (int)comboDPI.SelectedItem;
                _twain.CapSetDPI(sel);
            }
        }

        private void ckDuplex_CheckedChanged(object sender, EventArgs e)
        {
            if (!_loadingCaps && _twain.State == 4)
            {
                _twain.CapSetDuplex(ckDuplex.Checked);
            }
        }

        private void btnAllSettings_Click(object sender, EventArgs e)
        {
            var hand = new HandleRef(this, this.Handle);
            _twain.EnableSource(SourceEnableMode.ShowUIOnly, true, hand, SynchronizationContext.Current);
        }

        #endregion

        #endregion

    }
}
