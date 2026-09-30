dsl 1;
field user = toBool(1);
view "类型错误示例" { where true; within last(30m); group by user; timeline by startedAt; }
