﻿using System.Text.RegularExpressions;
using Mjml.Net;

namespace Wave.Services;

public partial class EmailTemplateService(ILogger<EmailTemplateService> logger) {
	public enum Constants {
		BrowserLink, HomeLink, ContentLogo, ContentTitle, ContentBody, EmailUnsubscribeLink
	}

	private ILogger<EmailTemplateService> Logger { get; } = logger;
	private IMjmlRenderer Renderer { get; } = new MjmlRenderer();
	
	private Regex TokenMatcher { get; } = MyRegex();

	public string Default(string url, string logoLink, string title, string body) {
		return Process("default", new Dictionary<Constants, object?> {
			{Constants.HomeLink, url},
			{Constants.ContentLogo, logoLink},
			{Constants.ContentTitle, title},
			{Constants.ContentBody, body}
		});
	}

	public string Process(string templateName, Dictionary<Constants, object?> data) {
		var options = new MjmlOptions {
			Beautify = false
		};

		string template = $"""
		                    <mjml>
		                      <mj-head>
		                        <mj-preview/>
		                      </mj-head>
		                      <mj-body>
		                        <mj-section>
		                        	<mj-column>
		                          	<mj-text align="center" font-size="13px" font-family="Ubuntu,Verdana">
		                              <a href="[[{Constants.BrowserLink}]]">Read in Browser</a></mj-text>
		                          </mj-column>
		                        </mj-section>
		                        <mj-section direction="rtl" padding-bottom="15px" padding-left="0px" padding-right="0px" padding-top="15px" padding="15px 0px 15px 0px">
		                          <mj-column vertical-align="middle" width="33%">
		                            <mj-image align="center" alt="" border-radius="0" border="none" container-background-color="transparent" height="auto" padding-bottom="5px" padding-left="5px" padding-right="5px" padding-top="5px" padding="5px 5px 5px 5px" href="[[{Constants.HomeLink}]]" src="[[{Constants.ContentLogo}]]"></mj-image>
		                          </mj-column>
		                          <mj-column vertical-align="middle" width="67%">
		                            <mj-text font-size="13px" font-family="Ubuntu,Verdana">
		                              <h1>[[{Constants.ContentTitle}]]</h1>
		                            </mj-text>
		                          </mj-column>
		                        </mj-section>
		                        <mj-section>
		                        	<mj-column>
		                          	<mj-divider border-width="1px"></mj-divider>
		                          </mj-column>
		                        </mj-section>
		                        <mj-section>
		                          <mj-column>
		                            <mj-text color="#55575d" font-size="13px" font-family="Ubuntu,Verdana">[[{Constants.ContentBody}]]</mj-text>
		                          </mj-column>
		                        </mj-section>
		                        <mj-section>
		                        	<mj-column>
		                          	<mj-divider border-width="1px"></mj-divider>
		                          </mj-column>
		                        </mj-section>
		                        <mj-section>
		                        	<mj-column>
		                            <mj-text align="center" font-size="13px" font-family="Ubuntu,Verdana">
		                              <a href="[[{Constants.EmailUnsubscribeLink}]]">Unsubscribe</a>
		                            </mj-text>
		                          </mj-column>
		                        </mj-section>
		                      </mj-body>
		                    </mjml>
		                    """;

		template = TokenMatcher.Replace(template, t => 
			data.TryGetValue(Enum.Parse<Constants>(t.Value[2..^2], true), out object? v) ? 
				v?.ToString() ?? "" : 
				"");
		
		(string html, var errors) = Renderer.Render(template, options);

		foreach (var error in errors) {
			Logger.LogWarning("Validation error in template {template}: [{position}] [{type}] {error}", 
				templateName, error.Position, error.Type, error.Error);
		}

		return html;
	}

	[GeneratedRegex(@"(\[\[.*?\]\])", 
		RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
	private static partial Regex MyRegex();
}