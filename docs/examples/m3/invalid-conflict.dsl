// 必须编译失败：头名不区分大小写，重复写同一头不能静默覆盖。
dsl 2;
rewrite "静态冲突" {
    priority 10;
    for host "api.test";
    when true;
    set header["X-Test"] = "one";
    remove header["x-test"];
}
