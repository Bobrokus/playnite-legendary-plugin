﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LegendaryLibraryNS.Models
{
    public class DownloadManagerData
    {
        public class Rootobject
        {
            public ObservableCollection<Download> downloads { get; set; }
        }

        public class Download : ObservableObject
        {
            public string gameID { get; set; }
            public string name { get; set; }
            public string installPath { get; set; }
            public string downloadSize { get; set; }
            public string installSize { get; set; }
            public long addedTime { get; set; }

            private long _completedTime;
            public long completedTime
            {
                get => _completedTime;
                set => SetValue(ref _completedTime, value);
            }

            public bool enableReordering;
            public int maxWorkers;
            public int maxSharedMemory;
            private int _status;
            public int status
            {
                get => _status;
                set => SetValue(ref _status, value);
            }
            public int downloadAction { get; set; }
            public List<string> extraContent { get; set; }
        }
    }
}
