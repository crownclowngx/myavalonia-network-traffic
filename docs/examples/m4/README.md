# M4 合成请求库样例

[requests.json](requests.json)是schema1请求库，包含一个JSON模板与两个普通环境，全部使用example.test和合成值。`RequestExampleTests`直接读取此文件，通过生产库校验、变量解析和离线准备验证两个环境；示例内容进入开发sourceHash。

用它理解`${baseUrl}`、普通变量、Query一次编码、JSON字符串插值与精确数字保留。UI目前没有文件导入命令，可以按[快速开始](../../quick-start/network-traffic-m4.md)逐项填写。本样例不是待覆盖用户请求库的配置，不提供凭据，不触发网络。

正文类型的schema1枚举值为None=0、Text=1、Json=2、Form=3、RawFile=4；文件资产必须由生产导入器创建，不能在JSON中填入任意本地路径。
