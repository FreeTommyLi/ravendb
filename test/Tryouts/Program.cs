﻿using System;
using System.Threading.Tasks;
using FastTests.Issues;
using FastTests.Server.Basic;
using Raven.Client.Documents;
using Raven.Client.Server;
using Raven.Client.Server.Operations;

namespace Tryouts
{
    public class Program
    {
        public static void Main(string[] args)
        {
            for (int i = 0; i < 10000; i++)
            {
                Console.WriteLine(i);

                using (var a = new SlowTests.Smuggler.LegacySmugglerTests())
                {
                    a.CanImportIndexesAndTransformers("SlowTests.Smuggler.Indexes_And_Transformers_3.5.ravendbdump").Wait();
                }
            }
        }
    }


}