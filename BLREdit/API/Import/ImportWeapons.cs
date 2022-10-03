﻿using System.Collections.Generic;

namespace BLREdit.Import;

public sealed class ImportWeapons
{
    public List<ImportItem> depot { get; set; }
    public List<ImportItem> primary { get; set; }
    public List<ImportItem> secondary { get; set; }

    public override string ToString()
    {
        return LoggingSystem.ObjectToTextWall(this);
    }
}