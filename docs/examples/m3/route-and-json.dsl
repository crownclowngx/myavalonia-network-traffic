// 供编译器和纯规划器验证，当前未接入代理 UI，运行示例不会发送网络请求。
dsl 2;
field caller = firstNonEmpty(first(original.headers["X-Caller"]), "normal");

rewrite "指定调用者切测试环境" {
    priority 10;
    for host "api.example.test";
    when caller == "tester-a";
    route to "https://stage.example.test:8443";
    set path "/v2/orders";
    set query["debug"] = "1";
    remove query["legacy"];
    set header["X-Environment"] = "stage";
    remove header["X-Old-Debug"];
}

rewrite "标记该调用者的JSON请求" {
    priority 20;
    for host "api.example.test";
    when caller == "tester-a" and original.method == "POST";
    set json("$.context.environment") = "stage";
    remove json("$.debugToken");
}
