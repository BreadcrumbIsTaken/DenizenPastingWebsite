﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using LiteDB;
using System.Threading;

namespace DenizenPastingWebsite
{
    public static class PasteDatabase
    {
        public static class Internal
        {
            public static LiteDatabase DB;

            public static ILiteCollection<Paste> PasteCollection;

            public class DataTracker
            {
                public int _id;

                public long Value;
            }

            public static ILiteCollection<DataTracker> DataCollection;

            public static DataTracker DataInstance;

            public static Object IDLocker = new Object();
        }

        /// <summary>
        /// Initializes the database handler.
        /// </summary>
        public static void Init()
        {
            if (!Directory.Exists("data"))
            {
                Directory.CreateDirectory("data");
            }
            Internal.DB = new LiteDatabase("data/pastes.db");
            Internal.PasteCollection = Internal.DB.GetCollection<Paste>("pastes");
            Internal.DataCollection = Internal.DB.GetCollection<Internal.DataTracker>("data");
            Internal.DataInstance = Internal.DataCollection.FindById(0);
            if (Internal.DataInstance == null)
            {
                Internal.DataInstance = new Internal.DataTracker() { Value = 0, _id = 0 };
            }
        }

        /// <summary>
        /// Gets the next paste ID, automatically incrementing the ID in the process.
        /// </summary>
        public static long GetNextPasteID()
        {
            lock (Internal.IDLocker)
            {
                long result = Internal.DataInstance.Value++;
                Internal.DataCollection.Upsert(0, Internal.DataInstance);
                return result;
            }
        }
    }
}
