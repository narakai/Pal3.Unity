﻿// ---------------------------------------------------------------------------------------------
//  Copyright (c) 2021-2023, Jiaqi Liu. All rights reserved.
//  See LICENSE file in the project root for license information.
// ---------------------------------------------------------------------------------------------

namespace Pal3.Command.SceCommands
{
    [AvailableInConsole]
    [SceCommand(250, "取消镜头锁定")]
    public class CameraFreeCommand : ICommand
    {
        public CameraFreeCommand(int free)
        {
            Free = free;
        }

        public int Free { get; }
    }
}