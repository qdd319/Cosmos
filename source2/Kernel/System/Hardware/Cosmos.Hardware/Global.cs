﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cosmos.Hardware {
    static public class Global {
        static public Keyboard Keyboard = new Keyboard();
        static public PIT PIT = new PIT();
        static public TextScreen TextScreen = new TextScreen();

        static public void Init() {
            Cosmos.Debug.Debugger.Send("Cosmos.Hardware.Global.Init");
            // Temp
            Cosmos.Hardware2.Global2.Init();
            Cosmos.Debug.Debugger.Send("Before Cosmos.Core.Global.Init");
            
            // End Temp
            Cosmos.Core.Global.Init();
        }

    }
}
