using System;

namespace InfinityHairartsAPI.Services;

public sealed record ReservationReply(string Item1, Guid Item2, string Item3, ReservationDraft? Draft);
