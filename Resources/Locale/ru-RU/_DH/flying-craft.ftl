# Dark Haven - лётная техника
flying-craft-jump-cooldown = БСС-привод ещё перезаряжается.
flying-craft-jump-no-fuel = Недостаточно блюспейс-топлива для прыжка.
flying-craft-jump-charging = Зарядка БСС-привода…

# --- попап смены оружия ---
flying-craft-weapon-selected = Выбрано орудие № { $index }.

# --- платы / починка ---
flying-craft-board-slot = слот платы
flying-craft-board-civilian = Гражданский корпус не принимает тир-плату — сначала вооружите его.
flying-craft-board-wrong-tier = Этому аппарату нужна плата тира { $tier }.
flying-craft-board-upgraded = Улучшено до тира { $tier }.
flying-craft-board-armed = Этот аппарат уже вооружён.
flying-craft-board-wrong-hull = Эта плата вооружения не подходит к этому корпусу.
flying-craft-board-weaponised = Плата вооружения установлена.
flying-craft-panel-open = Вы откручиваете техническую панель. Полёт отключён.
flying-craft-panel-closed = Вы закручиваете техническую панель.
flying-craft-repair-inserted = Вы устанавливаете компонент в корпус.
flying-craft-repair-enough = Этого компонента уже установлено достаточно.
flying-craft-repair-done = Корпус залатан (+100).
flying-craft-repair-needed = Для ремонта нужно: { $list }.
flying-craft-repair-ready = Все компоненты установлены.
flying-craft-mat-steel = стали
flying-craft-mat-lv = НВ-кабеля
flying-craft-mat-mv = СВ-кабеля
flying-craft-mat-hv = ВВ-кабеля
flying-craft-exit-too-fast = Аппарат движется слишком быстро, чтобы выбраться.

# --- консоли покупки ---
flying-craft-console-title = Магазин лётной техники
flying-craft-console-balance = Баланс: { $balance } спесо
flying-craft-console-spawn-header = Площадка спавна
flying-craft-console-spawn-none = Площадка не выбрана.
flying-craft-console-spawn-selected = Выбрано: { $name }
flying-craft-console-catalog-header = Каталог
flying-craft-console-price = { $price }
flying-craft-console-buy = Купить
flying-craft-console-pick-spawn = Сначала выберите площадку спавна.
flying-craft-console-bad-spawn = Площадка вне радиуса или недоступна.
flying-craft-console-no-money = Недостаточно средств.
flying-craft-console-purchased = Покупка совершена.

# --- кокпитный HUD ---
flyingcraft-hud-expand = ▲ Панель
flyingcraft-hud-collapse = ▼ Панель
flyingcraft-hud-ammo = Боезапас: { $count } / { $max }
flyingcraft-hud-reloading = Перезарядка…
flyingcraft-hud-health = Прочность: { $hp }%
flyingcraft-hud-fuel = Топливо: { $fuel }%
flyingcraft-hud-speed = Скорость: { $speed } т/с
flyingcraft-hud-coords = Координаты: { $X }, { $Y }
flyingcraft-hud-btn-exit = Выйти
flyingcraft-hud-btn-headlight = Фара
flyingcraft-hud-btn-swap = Орудие
flyingcraft-hud-btn-map = Карта

# --- лётная техника: классы (тир-1 базы; тиры повышаются платами) ---
ent-FlyingCraftFighter = Истребитель
    .desc = Одноместный боевой лётный аппарат. Сбалансированный, с лазером и автопушкой.
ent-FlyingCraftInterceptor = Перехватчик
    .desc = Одноместный боевой лётный аппарат. Очень манёвренный и быстрый, с фугасными ракетами и скорострельной винтовкой.
ent-FlyingCraftBomber = Бомбардировщик
    .desc = Одноместный боевой лётный аппарат. Самый прочный и медленный, с мощными ракетами и большим трюмом.
ent-FlyingCraftAttacker = Штурмовик
    .desc = Одноместный боевой лётный аппарат. Прочный, с автовинтовкой и структурными ракетами.
ent-FlyingCraftScout = Разведчик
    .desc = Одноместный лётный аппарат-разведчик. Самый быстрый, с увеличенным обзором и пулемётом.

ent-FlyingCraftCivilianHauler = гражданский грузовик
    .desc = Безоружный лётный аппарат с огромным трюмом. Можно вооружить платой (бомбардировщик/штурмовик/истребитель).
ent-FlyingCraftCivilianRunner = гражданский курьер
    .desc = Безоружный скоростной лётный аппарат. Можно вооружить платой (перехватчик/разведчик/истребитель).

ent-BaseFlyingCraft = лётный аппарат
    .desc = Одноместный боевой лётный аппарат.

# --- ракеты-снаряды ---
ent-DHRocketHeavy = тяжёлая ракета
ent-DHRocketStructuralRkt = структурная ракета
ent-DHRocketConcussiveRkt = фугасная ракета

# --- навигационные огни ---
ent-FlyingCraftNavLightPort = навигационный огонь
    .desc = навигационный огонь
    .suffix = левый борт, зелёный
ent-FlyingCraftNavLightStarboard = навигационный огонь
    .desc = навигационный огонь
    .suffix = правый борт, красный

# --- действия ---
ent-ActionFlyingCraftExit = Покинуть аппарат
    .desc = Выбраться из лётного аппарата.
ent-ActionFlyingCraftSwapWeapon = Сменить орудие
    .desc = Переключить активное носовое орудие.
ent-ActionFlyingCraftHeadlight = Фара
    .desc = Включить/выключить передний прожектор аппарата.

# Боевой режим — штатный (красная кнопка боевого режима персонажа): в нём аппарат
# доворачивается носом к курсору, а активное орудие стреляет при зажатой ЛКМ. Тормоз — Space.

# --- консоли и площадки ---
ent-ComputerFlyingCraftCivilian = магазин гражданской техники
    .desc = Покупка гражданского лётного аппарата со спавном на ближайшей гражданской площадке.
ent-ComputerFlyingCraftSecurity = магазин боевой техники
    .desc = Покупка боевого лётного аппарата Т1 со спавном на ближайшей охранной площадке.
ent-ComputerFlyingCraftBoards = магазин плат
    .desc = Покупка плат улучшения и вооружения для лётной техники.
ent-FlyingCraftCivilianSpawner = гражданская площадка
ent-FlyingCraftSecuritySpawner = охранная площадка

# --- платы ---
ent-FlyingCraftBoardTier2 = плата улучшенного аппарата
    .desc = Повышает лётный аппарат с тира 1 до тира 2.
ent-FlyingCraftBoardTier3 = плата тяжёлого аппарата
    .desc = Повышает лётный аппарат с тира 2 до тира 3.
ent-FlyingCraftBoardTier4 = плата сверхтяжёлого аппарата
    .desc = Повышает лётный аппарат с тира 3 до тира 4.
ent-FlyingCraftBoardTier5 = плата экспериментального аппарата
    .desc = Повышает лётный аппарат с тира 4 до тира 5.
ent-FlyingCraftWeaponBoardInterceptor = плата вооружения «Перехватчик»
    .desc = Вооружает скоростной гражданский аппарат как Перехватчик Т1.
ent-FlyingCraftWeaponBoardScout = плата вооружения «Разведчик»
    .desc = Вооружает скоростной гражданский аппарат как Разведчик Т1.
ent-FlyingCraftWeaponBoardFighterFast = плата вооружения «Истребитель» (лёгкий)
    .desc = Вооружает скоростной гражданский аппарат как Истребитель Т1.
ent-FlyingCraftWeaponBoardBomber = плата вооружения «Бомбардировщик»
    .desc = Вооружает тяжёлый гражданский аппарат как Бомбардировщик Т1.
ent-FlyingCraftWeaponBoardAttacker = плата вооружения «Штурмовик»
    .desc = Вооружает тяжёлый гражданский аппарат как Штурмовик Т1.
ent-FlyingCraftWeaponBoardFighterHeavy = плата вооружения «Истребитель» (тяжёлый)
    .desc = Вооружает тяжёлый гражданский аппарат как Истребитель Т1.
