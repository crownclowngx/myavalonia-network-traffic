// 后续规则仍读取原始头，不会读取第一条规则写出的 new。
dsl 2;
field caller = firstNonEmpty(first(original.headers["X-Caller"]), "normal");
rewrite "改头" {
    priority 10;
    for host "api.test";
    when true;
    set header["X-Caller"] = "new";
}
rewrite "保存原始判定" {
    priority 20;
    for host "api.test";
    when caller == "old";
    set header["X-Original-Caller"] = caller;
}
