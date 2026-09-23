from pathlib import Path

from docx import Document
from docx.enum.table import WD_CELL_VERTICAL_ALIGNMENT
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.shared import Inches, Pt, RGBColor

OUTPUT = Path(__file__).with_name("星桥智造智能巡检试点方案V1.0.docx")
WIDTH = 9360
INK, BLUE, DARK, MUTED = "0B2545", "2E74B5", "1F4D78", "64748B"


def font(run, size=11, bold=False, color="000000", italic=False):
    run.font.name = "Calibri"
    fonts = run._element.get_or_add_rPr().rFonts
    for key, value in (("w:ascii", "Calibri"), ("w:hAnsi", "Calibri"), ("w:eastAsia", "Microsoft YaHei")):
        fonts.set(qn(key), value)
    run.font.size, run.bold, run.italic = Pt(size), bold, italic
    run.font.color.rgb = RGBColor.from_string(color)


def styles(doc):
    normal = doc.styles["Normal"]
    normal.font.name, normal.font.size = "Calibri", Pt(11)
    normal._element.rPr.rFonts.set(qn("w:eastAsia"), "Microsoft YaHei")
    normal.paragraph_format.space_after, normal.paragraph_format.line_spacing = Pt(6), 1.10
    for name, size, color, before, after in (
        ("Heading 1", 16, BLUE, 16, 8),
        ("Heading 2", 13, BLUE, 12, 6),
        ("Heading 3", 12, DARK, 8, 4),
    ):
        style = doc.styles[name]
        style.font.name, style.font.size, style.font.bold = "Calibri", Pt(size), True
        style._element.rPr.rFonts.set(qn("w:eastAsia"), "Microsoft YaHei")
        style.font.color.rgb = RGBColor.from_string(color)
        style.paragraph_format.space_before, style.paragraph_format.space_after = Pt(before), Pt(after)
        style.paragraph_format.keep_with_next = True


def para(doc, text, lead=None):
    p = doc.add_paragraph()
    p.paragraph_format.space_after, p.paragraph_format.line_spacing = Pt(6), 1.10
    if lead and text.startswith(lead):
        font(p.add_run(lead), bold=True, color=INK)
        font(p.add_run(text[len(lead):]))
    else:
        font(p.add_run(text))


def bullet_numbering(doc):
    root = doc.part.numbering_part.element
    abstract_id = max([int(x.get(qn("w:abstractNumId"))) for x in root.findall(qn("w:abstractNum"))], default=0) + 1
    num_id = max([int(x.get(qn("w:numId"))) for x in root.findall(qn("w:num"))], default=0) + 1
    abstract = OxmlElement("w:abstractNum")
    abstract.set(qn("w:abstractNumId"), str(abstract_id))
    multi = OxmlElement("w:multiLevelType"); multi.set(qn("w:val"), "singleLevel"); abstract.append(multi)
    level = OxmlElement("w:lvl"); level.set(qn("w:ilvl"), "0")
    for tag, value in (("w:start", "1"), ("w:numFmt", "bullet"), ("w:lvlText", "•"), ("w:suff", "tab")):
        node = OxmlElement(tag); node.set(qn("w:val"), value); level.append(node)
    ppr = OxmlElement("w:pPr")
    ind = OxmlElement("w:ind"); ind.set(qn("w:left"), "720"); ind.set(qn("w:hanging"), "360"); ppr.append(ind)
    spacing = OxmlElement("w:spacing"); spacing.set(qn("w:after"), "160"); spacing.set(qn("w:line"), "280"); spacing.set(qn("w:lineRule"), "auto"); ppr.append(spacing)
    level.append(ppr); abstract.append(level); root.append(abstract)
    num = OxmlElement("w:num"); num.set(qn("w:numId"), str(num_id))
    ref = OxmlElement("w:abstractNumId"); ref.set(qn("w:val"), str(abstract_id)); num.append(ref); root.append(num)
    return num_id


def bullet(doc, text, num_id):
    p = doc.add_paragraph()
    numpr = OxmlElement("w:numPr")
    level = OxmlElement("w:ilvl"); level.set(qn("w:val"), "0")
    num = OxmlElement("w:numId"); num.set(qn("w:val"), str(num_id))
    numpr.extend([level, num]); p._p.get_or_add_pPr().append(numpr)
    p.paragraph_format.space_after, p.paragraph_format.line_spacing = Pt(8), 1.167
    font(p.add_run(text))


def cell(cell_obj, text, header=False, align=WD_ALIGN_PARAGRAPH.LEFT):
    cell_obj.text, cell_obj.vertical_alignment = str(text), WD_CELL_VERTICAL_ALIGNMENT.CENTER
    tcpr = cell_obj._tc.get_or_add_tcPr()
    margins = OxmlElement("w:tcMar")
    for edge, value in (("top", 80), ("bottom", 80), ("start", 120), ("end", 120)):
        node = OxmlElement(f"w:{edge}"); node.set(qn("w:w"), str(value)); node.set(qn("w:type"), "dxa"); margins.append(node)
    tcpr.append(margins)
    if header:
        shade = OxmlElement("w:shd"); shade.set(qn("w:fill"), "F2F4F7"); tcpr.append(shade)
    for p in cell_obj.paragraphs:
        p.alignment, p.paragraph_format.space_after, p.paragraph_format.line_spacing = align, Pt(0), 1.08
        for run in p.runs: font(run, size=9.5, bold=header, color=INK if header else "000000")


def geometry(table, widths):
    assert sum(widths) == WIDTH
    table.autofit = False
    tblpr = table._tbl.tblPr
    for tag, attrs in (("w:tblW", (("w:w", WIDTH), ("w:type", "dxa"))), ("w:tblInd", (("w:w", 120), ("w:type", "dxa"))), ("w:tblLayout", (("w:type", "fixed"),))):
        node = OxmlElement(tag)
        for key, value in attrs: node.set(qn(key), str(value))
        tblpr.append(node)
    borders = OxmlElement("w:tblBorders")
    for edge in ("top", "left", "bottom", "right", "insideH", "insideV"):
        node = OxmlElement(f"w:{edge}"); node.set(qn("w:val"), "single"); node.set(qn("w:sz"), "6"); node.set(qn("w:color"), "D7DEE7"); borders.append(node)
    tblpr.append(borders)
    grid = table._tbl.tblGrid
    for child in list(grid): grid.remove(child)
    for value in widths:
        col = OxmlElement("w:gridCol"); col.set(qn("w:w"), str(value)); grid.append(col)
    for row in table.rows:
        for index, item in enumerate(row.cells):
            tcw = OxmlElement("w:tcW"); tcw.set(qn("w:w"), str(widths[index])); tcw.set(qn("w:type"), "dxa"); item._tc.get_or_add_tcPr().append(tcw)


def matrix(doc, headers, rows, widths, aligns=None):
    table = doc.add_table(rows=1, cols=len(headers))
    for i, value in enumerate(headers): cell(table.rows[0].cells[i], value, True, WD_ALIGN_PARAGRAPH.CENTER)
    for values in rows:
        row = table.add_row()
        for i, value in enumerate(values): cell(row.cells[i], value, align=aligns[i] if aligns else WD_ALIGN_PARAGRAPH.LEFT)
    geometry(table, widths)
    repeat = OxmlElement("w:tblHeader"); repeat.set(qn("w:val"), "true"); table.rows[0]._tr.get_or_add_trPr().append(repeat)
    doc.add_paragraph().paragraph_format.space_after = Pt(2)


def page_number(paragraph):
    paragraph.alignment = WD_ALIGN_PARAGRAPH.RIGHT
    font(paragraph.add_run("第 "), 9, color=MUTED)
    run = paragraph.add_run()
    begin = OxmlElement("w:fldChar"); begin.set(qn("w:fldCharType"), "begin")
    instr = OxmlElement("w:instrText"); instr.set(qn("xml:space"), "preserve"); instr.text = " PAGE "
    separate = OxmlElement("w:fldChar"); separate.set(qn("w:fldCharType"), "separate")
    value = OxmlElement("w:t"); value.text = "1"
    end = OxmlElement("w:fldChar"); end.set(qn("w:fldCharType"), "end")
    run._r.extend([begin, instr, separate, value, end]); font(paragraph.add_run(" 页"), 9, color=MUTED)


def build():
    doc = Document(); section = doc.sections[0]
    section.page_width, section.page_height = Inches(8.5), Inches(11)
    section.top_margin = section.right_margin = section.bottom_margin = section.left_margin = Inches(1)
    section.header_distance = section.footer_distance = Inches(0.492)
    styles(doc); bullet_id = bullet_numbering(doc)
    font(section.header.paragraphs[0].add_run("星桥智造 | 华东20店智能巡检上线"), 9, True, MUTED)
    page_number(section.footer.paragraphs[0])

    p = doc.add_paragraph(); p.paragraph_format.space_after = Pt(3); font(p.add_run("项目决策简报 · DEMO DATA"), 10, True, BLUE)
    p = doc.add_paragraph(); p.paragraph_format.space_after = Pt(4); font(p.add_run("智能巡检试点方案 V1.0"), 25, True, INK)
    p = doc.add_paragraph(); p.paragraph_format.space_after = Pt(16); font(p.add_run("从3家灰度门店扩展至华东20家门店的上线与验收计划"), 13, color=MUTED)
    for label, value in (("项目代号", "STAR-BRIDGE-20"), ("目标上线", "2026年9月18日"), ("项目负责人", "洪恩泽（项目负责人）"), ("当前阶段", "灰度上线准备"), ("文档性质", "模拟演示数据，不对应任何真实客户")):
        p = doc.add_paragraph(); p.paragraph_format.space_after = Pt(2); font(p.add_run(f"{label}："), bold=True, color=INK); font(p.add_run(value), color="334155")
    rule = doc.add_paragraph(); rule.paragraph_format.space_before, rule.paragraph_format.space_after = Pt(8), Pt(12)
    pborder = OxmlElement("w:pBdr"); bottom = OxmlElement("w:bottom")
    for key, value in (("w:val", "single"), ("w:sz", "16"), ("w:space", "1"), ("w:color", BLUE)): bottom.set(qn(key), value)
    pborder.append(bottom); rule._p.get_or_add_pPr().append(pborder)

    doc.add_heading("1. 决策摘要", 1)
    para(doc, "本项目计划在华东区域20家门店上线智能设备巡检与分级告警能力。采用“3家灰度、48小时观察、分两批扩容”的策略，把故障发现平均耗时从4小时缩短至30分钟以内。")
    para(doc, "当前建议：维持9月18日目标日期，但必须在9月10日前关闭三项门槛：告警误报率不高于3%、弱网重试链路通过、企业微信通知达到99%的可送达率。若任一门槛未达标，只上线3家灰度门店。", "当前建议：")
    doc.add_heading("2. 项目目标与范围", 1)
    for text in ("覆盖上海、杭州、苏州共20家门店的巡检设备、网络状态和核心业务接口。", "建立P0/P1/P2三级告警：P0五分钟内响应，P1三十分钟内响应，P2进入次日优化池。", "告警通过企业微信触达值班人员，并在项目系统形成可追溯任务。", "形成上线清单、运维SOP、培训记录和上线后一周复盘报告。"):
        bullet(doc, text, bullet_id)
    para(doc, "不在范围：设备采购、门店网络改造、消费者身份数据分析，以及未经审批的自动关机或远程重启。", "不在范围：")
    doc.add_heading("3. 里程碑与当前状态", 1)
    matrix(doc, ["里程碑", "负责人", "日期", "状态", "验收信号"], [("门店清单与设备基线", "林悦", "09-05", "80%", "20店设备、网络和责任人完整"), ("告警规则评审", "周恺", "09-08", "65%", "误报率压测不高于3%"), ("企业微信通知联调", "顾宁", "09-10", "30%", "100次测试送达率不低于99%"), ("3店灰度上线", "林悦", "09-12", "未开始", "连续48小时无P0事故"), ("20店分批扩容", "洪恩泽", "09-18", "未开始", "全部门店签署上线确认")], [2100, 1050, 1000, 1350, 3860], [WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.LEFT])

    doc.add_page_break(); doc.add_heading("4. 关键业务规则", 1)
    matrix(doc, ["级别", "触发条件", "响应", "处理要求"], [("P0", "收银、支付或核心接口完全不可用", "5分钟", "通知值班负责人并创建高优先级任务"), ("P1", "设备连续离线10分钟或关键指标异常", "30分钟", "通知区域运维并记录排查证据"), ("P2", "单点抖动、容量趋势或非阻断异常", "次日", "进入优化池，由项目例会统一排序")], [900, 3000, 1100, 4360], [WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.LEFT])
    doc.add_heading("4.1 Agent自动化边界", 2)
    for text in ("Agent可以读取已授权的项目、任务、会议和本方案，生成风险判断和推进建议。", "添加普通任务评论属于低风险动作，可以在授权后直接执行。", "创建任务或会议行动项必须经过独立AI审核；信息不足时升级人工。", "修改任务状态、负责人、截止时间或项目资料属于高风险动作，必须人工审批。", "任何删除、远程设备控制和生产配置修改均不向演示Agent开放。"):
        bullet(doc, text, bullet_id)
    doc.add_heading("5. 风险登记册", 1)
    matrix(doc, ["风险", "等级", "当前证据", "负责人", "缓解措施"], [("弱网导致心跳误判", "高", "浦东店晚高峰丢包明显", "林悦", "4G备链演练与离线窗口压测"), ("告警规则过敏", "高", "首轮模拟误报率4.2%", "周恺", "90秒去重窗口并按门店分层"), ("通知接收人缺失", "中", "夜间升级链路待验证", "顾宁", "补齐双联系人和超时升级"), ("扩容节奏过快", "中", "尚未完成3店灰度", "洪恩泽", "3店观察48小时后再分批扩容")], [1850, 750, 2460, 900, 3400], [WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.LEFT])
    doc.add_heading("6. 责任分工", 1)
    matrix(doc, ["角色", "姓名", "主要职责", "升级条件"], [("项目负责人", "洪恩泽", "范围、优先级、上线门槛和最终验收", "门槛未达标或出现P0"), ("平台工程师", "周恺", "采集、去重、通知接口与技术证据", "误报率高于3%"), ("运营负责人", "顾宁", "告警通知、联系人和培训组织", "送达率低于99%"), ("实施经理", "林悦", "设备基线、灰度排期和门店沟通", "影响营业或集中投诉")], [1500, 900, 3960, 3000], [WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.LEFT])

    doc.add_page_break(); doc.add_heading("7. 验收标准", 1)
    for text in ("功能：20家门店均能完成设备在线检测、告警分级和任务留痕。", "性能：巡检结果写入延迟P95不高于60秒，告警通知P95不高于90秒。", "质量：告警误报率不高于3%，企业微信测试送达率不低于99%。", "安全：Agent敏感修改全部进入人工审批，越权测试不产生业务写入。", "运营：20家门店联系人、培训记录和上线确认完整可查。"):
        bullet(doc, text, bullet_id)
    doc.add_heading("8. 已确认决策", 1)
    matrix(doc, ["日期", "决策", "理由", "复核点"], [("09-02", "采用3店灰度后分批扩容", "降低一次性上线风险", "灰度48小时后评估告警"), ("09-02", "P0必须人工确认关闭", "避免自动化掩盖事故", "检查关闭人和证据"), ("09-03", "保留9月18日目标日期", "核心链路仍有修复窗口", "9月10日检查三项门槛")], [1100, 2800, 2700, 2760], [WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.LEFT])
    doc.add_heading("9. 未来7天行动", 1)
    for text in ("9月5日前补齐20家门店设备基线、值班人与夜班联系人。", "9月8日前完成告警规则第二轮压测，把误报率从4.2%降至3%以内。", "9月10日前完成弱网重试与100次企业微信通知联调，提交可核验证据。", "9月11日召开上线门禁会，只在三项门槛全部通过后批准3店灰度。"):
        bullet(doc, text, bullet_id)
    doc.add_heading("10. 演示说明", 1)
    para(doc, "本文件专为“撒豆成兵”Agent演示创建。所有企业、项目、门店、人员、指标和日期均为模拟数据。演示重点是：Agent只能读取被授权资料；关键结论能够引用项目、任务、会议和本方案；任何高风险写入必须等待人工审批。")
    props = doc.core_properties; props.title = "星桥智造智能巡检试点方案 V1.0"; props.subject = "撒豆成兵 Agent 演示项目资料"; props.author = "撒豆成兵演示环境"; props.keywords = "Agent, 项目管理, 智能巡检, 演示数据"
    doc.save(OUTPUT); print(OUTPUT)


if __name__ == "__main__":
    build()
