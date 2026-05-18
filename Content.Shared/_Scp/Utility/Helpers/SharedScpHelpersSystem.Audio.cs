namespace Content.Shared._Scp.Utility.Helpers;

public abstract partial class SharedScpHelpersSystem
{
    /// <summary>
    /// Вычисляет плавно сглаженную громкость в dB по расстоянию с логарифмическим падением.
    /// </summary>
    /// <param name="distance">Текущее расстояние от источника</param>
    /// <param name="minDistance">Расстояние, на котором громкость = nearDb</param>
    /// <param name="maxDistance">Расстояние, на котором громкость = farDb</param>
    /// <param name="nearDb">Громкость у источника в dB</param>
    /// <param name="farDb">Громкость на пределе слышимости в dB</param>
    /// <param name="previousDb">Предыдущая громкость в dB (для сглаживания)</param>
    /// <param name="deltaTime">Время между кадрами</param>
    /// <param name="smoothing">Скорость сглаживания (чем больше, тем быстрее подстраивается)</param>
    /// <param name="curveA">Форма логарифмической кривой (0 = линейно, >0 = логарифмически)</param>
    /// <returns>Плавно сглаженная громкость в dB</returns>
    public static float SmoothVolumeDb(
        float distance,
        float minDistance,
        float maxDistance,
        float nearDb,
        float farDb,
        ref float previousDb,
        float deltaTime,
        float smoothing = 5f,
        float curveA = 2f)
    {
        // --- Логарифмическая интерполяция ---
        float t;
        if (distance <= minDistance)
            t = 0f;
        else if (distance >= maxDistance)
            t = 1f;
        else
            t = (distance - minDistance) / (maxDistance - minDistance);

        var shaped = MathF.Abs(curveA) < 1e-6f
            ? t
            : MathF.Log(1f + curveA * t) / MathF.Log(1f + curveA);

        var targetDb = MathHelper.Lerp(nearDb, farDb, shaped);

        // --- Плавное сглаживание ---
        var lerpFactor = MathHelper.Clamp(smoothing * deltaTime, 0f, 1f);
        previousDb = MathHelper.Lerp(previousDb, targetDb, lerpFactor);

        return previousDb;
    }
}
