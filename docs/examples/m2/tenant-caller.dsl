dsl 1;
// 业务身份可来自不同位置；未知来源不会被后续值掩盖。
field tenant = first(original.query["tenant"]);
field caller = firstNonEmpty(
    first(original.query["user"]),
    first(original.headers["x-business-user"]),
    first(original.cookies["business_user"]),
    toString(original.json("$.identity.user")),
    first(original.form["user"]),
    if(startsWith(original.path, "/actors/"), original.pathSegment(1), null)
);
view "调用过程" {
    where source == "capture";
    within last(30m);
    group by tuple(tenant,caller);
    columns tenant,caller;
    timeline by startedAt;
}
