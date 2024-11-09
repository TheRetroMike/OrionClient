﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools
{
    public class Ec1ipsePool : OreHQPool
    {
        public override string PoolName { get; } = "Ec1ipse [[Unofficial]]";
        public override string DisplayName => PoolName;
        public override string Description { get; } = "Unofficial pool implemented provided by Ecl1ipse. 5% commission";

        public override Dictionary<string, string> Features { get; } = new Dictionary<string, string>();

        public override bool HideOnPoolList { get; } = false;
        public override string HostName { get; } = "ec1ipse.me";
    }
}
