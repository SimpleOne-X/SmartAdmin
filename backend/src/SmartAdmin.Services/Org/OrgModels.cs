namespace SmartAdmin.Services;

/// <summary>机构新增/编辑入参</summary>
public record OrgInput
{
    /// <summary>父机构 Id(0=根,即顶级机构无父)</summary>
    public long ParentId { get; init; }

    /// <summary>机构名称</summary>
    public string Name { get; init; } = "";

    /// <summary>机构编码(唯一;新增时留空则后端自动生成)</summary>
    public string? Code { get; init; }

    /// <summary>机构分类(org_category 字典 value,可空)</summary>
    public string? Category { get; init; }

    /// <summary>排序(小在前)</summary>
    public int Sort { get; init; }

    /// <summary>是否启用</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>机构选择器选项:只有拼树所需的三个字段。</summary>
public record OrgOption
{
    public required long Id { get; init; }

    /// <summary>父机构 Id(0=根)</summary>
    public required long ParentId { get; init; }

    public required string Name { get; init; }
}

/// <summary>复制机构入参</summary>
public record OrgCopyInput
{
    /// <summary>克隆后根节点名称(留空则追加"-副本"后缀)</summary>
    public string? Name { get; init; }
}
