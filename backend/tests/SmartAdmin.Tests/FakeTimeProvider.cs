namespace SmartAdmin.Tests;

/// <summary>
/// 可推进的测试时钟。内核不引 <c>Microsoft.Extensions.TimeProvider.Testing</c>(测试项目也跟着不引),
/// 手写这一个即够:构造给起点,<see cref="Advance"/> 往前推;可选传入本地时区,让 <see cref="TimeProvider.GetLocalNow"/> 不随机器时区变。
/// </summary>
internal sealed class FakeTimeProvider(DateTimeOffset start, TimeZoneInfo? localTimeZone = null) : TimeProvider
{
    private DateTimeOffset _utc = start;

    public override TimeZoneInfo LocalTimeZone => localTimeZone ?? base.LocalTimeZone;

    public override DateTimeOffset GetUtcNow() => _utc;

    public void Advance(TimeSpan delta) => _utc += delta;
}
