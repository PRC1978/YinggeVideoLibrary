namespace YinggeVideoLibrary;

internal static class RolePersonKind
{
    public const string Unspecified = "unspecified";
    public const string Director = "director";
    public const string MaleActor = "male-actor";
    public const string FemaleActor = "female-actor";
    public const string Other = "other";

    public static bool IsValid(string? value) => value is Unspecified or Director or MaleActor or FemaleActor or Other;

    public static string Display(string? value) => value switch
    {
        Director => "导演",
        MaleActor => "男演员",
        FemaleActor => "女演员",
        Other => "其他人物",
        _ => "未分类"
    };
}
