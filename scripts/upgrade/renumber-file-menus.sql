-- 「文件管理」目录(600)下两个页面的菜单编号对调:
--   文件路径  620–629 → 610–619      文件管理  610–615 → 620–625
-- 页面、它们的按钮、sys_role_menu 与 sys_user_menu 里指向它们的授权一起改,角色和用户的授权不丢。
--
-- 适用:SQLite / MySQL / SQL Server / PostgreSQL(只用四家共有的写法,标识符不加引号)。
-- 用法:停掉应用 → 备份库 → 执行本脚本 → 升级并启动新版本。必须在新版本第一次启动之前执行。
-- 10.26 及更早的库(只有「文件管理」、还没有「文件路径」)同样要跑:它会把「文件管理」挪到 620 段,给新版本腾出 610 段。
--
-- 闸门:只有「610 还是旧的文件管理页」、且新版本没有在旧编号上插过行(613、616–619 不存在)时才动手;
-- 已经迁移过的库、全新库、已经被新版本启动过的库,整个脚本什么都不改。
-- 最后一条 SELECT 把结果列出来:610 应是「文件路径」、620 应是「文件管理」;
-- 若 610 仍是「文件管理」,说明闸门没放行(新版本已启动过),请从备份恢复后按上面的顺序重来。
--
-- 建议放进事务里执行,在最前面补一行、最后补 COMMIT:
--   SQLite / PostgreSQL:BEGIN;    MySQL:START TRANSACTION;    SQL Server:BEGIN TRANSACTION;
-- 不用事务也行:各语句自带同一道闸门,闸门之后落位的几条只认负数 Id,中途失败重跑一遍即可补完。
--
-- 闸门写成 DISTINCT 派生表,是为了绕开 MySQL 不许在 UPDATE 里直接查被更新表的限制。

-- 1. 授权先停到负数 Id 上,避免对调时撞主键 / 唯一索引。
UPDATE sys_role_menu SET MenuId = -MenuId
 WHERE (MenuId BETWEEN 610 AND 615 OR MenuId BETWEEN 620 AND 629)
   AND EXISTS (SELECT 1 FROM (SELECT DISTINCT Id, Path FROM sys_menu) g WHERE g.Id = 610 AND g.Path = '/system/file')
   AND NOT EXISTS (SELECT 1 FROM (SELECT DISTINCT Id FROM sys_menu) s WHERE s.Id IN (613, 616, 617, 618, 619));

UPDATE sys_user_menu SET MenuId = -MenuId
 WHERE (MenuId BETWEEN 610 AND 615 OR MenuId BETWEEN 620 AND 629)
   AND EXISTS (SELECT 1 FROM (SELECT DISTINCT Id, Path FROM sys_menu) g WHERE g.Id = 610 AND g.Path = '/system/file')
   AND NOT EXISTS (SELECT 1 FROM (SELECT DISTINCT Id FROM sys_menu) s WHERE s.Id IN (613, 616, 617, 618, 619));

-- 2. 菜单:页面与按钮的 Id 停到负数,同一条语句里把子节点的 ParentId 对调(挂在这两个页面下的自建按钮跟着走)。
--    Sort:两个页面还是种子默认值(文件管理 1、文件路径 2)时对调,让「文件路径」排在前面;管理员改过的值不动。
--    Sort 必须写在 Id 前面,MySQL 按书写顺序依次赋值,写在后面就读到已经改掉的 Id。
UPDATE sys_menu
   SET Sort     = CASE WHEN Id = 610 AND Sort = 1 THEN 2 WHEN Id = 620 AND Sort = 2 THEN 1 ELSE Sort END,
       Id       = CASE WHEN Id BETWEEN 610 AND 615 OR Id BETWEEN 620 AND 629 THEN -Id ELSE Id END,
       ParentId = CASE ParentId WHEN 610 THEN 620 WHEN 620 THEN 610 ELSE ParentId END
 WHERE (Id BETWEEN 610 AND 615 OR Id BETWEEN 620 AND 629 OR ParentId IN (610, 620))
   AND EXISTS (SELECT 1 FROM (SELECT DISTINCT Id, Path FROM sys_menu) g WHERE g.Id = 610 AND g.Path = '/system/file')
   AND NOT EXISTS (SELECT 1 FROM (SELECT DISTINCT Id FROM sys_menu) s WHERE s.Id IN (613, 616, 617, 618, 619));

-- 3. 负数 Id 落到新位置:文件管理 -610…-615 → 620…625,文件路径 -620…-629 → 610…619。
UPDATE sys_menu      SET Id     = 10 - Id      WHERE Id     BETWEEN -615 AND -610;
UPDATE sys_menu      SET Id     = -Id - 10     WHERE Id     BETWEEN -629 AND -620;
UPDATE sys_role_menu SET MenuId = 10 - MenuId  WHERE MenuId BETWEEN -615 AND -610;
UPDATE sys_role_menu SET MenuId = -MenuId - 10 WHERE MenuId BETWEEN -629 AND -620;
UPDATE sys_user_menu SET MenuId = 10 - MenuId  WHERE MenuId BETWEEN -615 AND -610;
UPDATE sys_user_menu SET MenuId = -MenuId - 10 WHERE MenuId BETWEEN -629 AND -620;

-- 4. 核对:600 目录下应是 610 文件路径(Sort 1)与 620 文件管理(Sort 2),按钮的 ParentId 分别是 610 / 620,没有负数 Id。
SELECT Id, ParentId, Title, Sort, Path FROM sys_menu WHERE Id BETWEEN 600 AND 629 OR Id < 0 ORDER BY Id;
