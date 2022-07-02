using System.Collections.Generic;

namespace GamepadSelection
{
    public class TrustMembers
    {
        public static Dictionary<string, uint> members = new Dictionary<string, uint> {
            // 5.0
            {"水晶公", 1073782279},
            {"莱楠", 1073782277},
            {"阿莉塞0", 1073782278},
            
            // 6.0
            {"古·拉哈·提亚", 1073819510},
            {"桑克瑞德", 1073819511},
            {"阿尔菲诺", 1073819509},
            {"埃斯蒂尼安", 1073819507},
            {"雅·修特拉", 1073819506},
            {"于里昂热", 1073819508},
            {"阿莉塞", 1073819505},
        };

        public static uint GetObjectIdByName(string name)
        {
            if (TrustMembers.members.ContainsKey(name))
                return TrustMembers.members[name];
            
            return 0;
        }
    }
}