namespace SteamDB.Core;

public readonly record struct SliceAssessment(SliceStatus Status, string Reason);

public static class SlicePlanner
{
    public static SliceAssessment Assess(
        int? totalCount,
        int pagesFetched,
        int pageSize,
        int maxPages,
        bool canSplitFurther,
        bool reachedBeforeTargetYear = false)
    {
        if (totalCount == 0 && pagesFetched > 0)
            return new(SliceStatus.Empty, "Поиск вернул ноль игр.");
        if (reachedBeforeTargetYear && pagesFetched > 0)
            return new(SliceStatus.Complete, "Выдача дошла до игр старше нужного года.");
        if (totalCount is > 0 && pagesFetched * Math.Max(1, pageSize) >= totalCount.Value)
            return new(SliceStatus.Complete, "Все страницы выдачи прочитаны.");
        if (pagesFetched >= Math.Max(1, maxPages) && (totalCount is null || totalCount > pagesFetched * Math.Max(1, pageSize)))
        {
            if (canSplitFurther)
                return new(SliceStatus.Split, "Список длиннее лимита страниц: фильтр нужно дробить.");
            return new(SliceStatus.Deferred, "Список длиннее лимита страниц и дальше не дробится: отложен, не пустой.");
        }

        if (pagesFetched <= 0)
            return new(SliceStatus.Pending, "Страницы ещё не читались.");
        return new(SliceStatus.Partial, "Есть следующая страница.");
    }
}
